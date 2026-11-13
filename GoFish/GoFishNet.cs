// ============================================================================
//  GO FISH  —  2 to 4 PLAYERS  •  NETWORK + CPU OPPONENTS
//  C# / .NET 10 + Avalonia 11.3.x   (Windows • macOS • Linux)
//
//  One file, its own Main(). Every card is drawn by hand with DrawingContext,
//  so there are zero image assets.
//
//  ARCHITECTURE — authoritative host
//    The host process owns the deck, every hand, and all rule evaluation.
//    Each seat is independently either the local human (seat 0), a networked
//    human, or a CPU at Easy / Normal / Hard. Clients own nothing but pixels:
//    they send one message type ("ask") and receive snapshots written from
//    their own point of view, in which other hands are a COUNT, never a list
//    of card ids. No shared RNG, no lockstep, no desync, no peeking.
//
//  TRANSPORT
//    TCP 34780, newline-delimited JSON.  UDP 34781 for LAN discovery.
//
//  CSPROJ  (namespace is GoFishNet, so StartupObject is unchanged)
//    <Project Sdk="Microsoft.NET.Sdk">
//      <PropertyGroup>
//        <OutputType>WinExe</OutputType>
//        <TargetFramework>net10.0</TargetFramework>
//        <Nullable>disable</Nullable>
//        <ImplicitUsings>disable</ImplicitUsings>
//        <StartupObject>GoFishNet.Program</StartupObject>
//      </PropertyGroup>
//      <ItemGroup>
//        <PackageReference Include="Avalonia"               Version="11.3.22" />
//        <PackageReference Include="Avalonia.Desktop"       Version="11.3.22" />
//        <PackageReference Include="Avalonia.Themes.Fluent" Version="11.3.22" />
//      </ItemGroup>
//    </Project>
//
//  PLAYING
//    Host clicks HOST, sets seats 1-3 to Closed / Open / CPU Easy|Normal|Hard,
//    waits for any Open seats to fill, then clicks START.
//    Others click JOIN and type the host's IP, or click FIND to auto-discover.
//    Solo is fine: set all three seats to CPU and start immediately.
//
//  CONTROLS
//    Click a card to choose a rank, then click an opponent to ask them.
//    (With only one opponent the ask fires immediately.)  Esc clears.
//    R restarts when the game is over (host only).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace GoFishNet
{
    // =====================================================================
    //  MODEL
    // =====================================================================

    public enum Suit { Clubs = 0, Diamonds = 1, Hearts = 2, Spades = 3 }

    public enum SeatKind { Closed = 0, Open = 1, CpuEasy = 2, CpuNormal = 3, CpuHard = 4, Human = 5 }

    public sealed class Card
    {
        public int Rank { get; }              // 1 = Ace .. 13 = King
        public Suit Suit { get; }
        public Card(int rank, Suit suit) { Rank = rank; Suit = suit; }

        // Cards travel the wire as a single int so the protocol stays flat.
        public int Id => (Rank - 1) * 4 + (int)Suit;
        public static Card FromId(int id) => new Card(id / 4 + 1, (Suit)(id % 4));

        static readonly string[] Labels =
            { "", "A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K" };
        static readonly string[] Words =
            { "", "Aces", "Twos", "Threes", "Fours", "Fives", "Sixes", "Sevens",
              "Eights", "Nines", "Tens", "Jacks", "Queens", "Kings" };

        public string Label => Labels[Rank];
        public static string LabelOf(int rank) => Labels[rank];
        public static string WordOf(int rank) => Words[rank];

        public string Glyph => Suit switch
        {
            Suit.Clubs => "\u2663",
            Suit.Diamonds => "\u2666",
            Suit.Hearts => "\u2665",
            _ => "\u2660"
        };

        public bool IsRed => Suit == Suit.Diamonds || Suit == Suit.Hearts;
    }

    // =====================================================================
    //  WIRE PROTOCOL
    //  One DTO for everything. Both ends run identical code, so casing
    //  never has to be negotiated.
    // =====================================================================

    public sealed class Msg
    {
        public string T { get; set; }          // hello|ask|restart | lobby|state|ev|kick

        // client -> host
        public string Name { get; set; }
        public int Rank { get; set; }
        public int Target { get; set; }

        // lobby (host -> client)
        public string[] SeatNames { get; set; }
        public string[] SeatKinds { get; set; }

        // snapshot (host -> client, always from the RECIPIENT's perspective)
        public int You { get; set; }
        public int[] Hand { get; set; }        // recipient's cards only
        public string[] Names { get; set; }
        public int[] Counts { get; set; }      // every seat's card COUNT
        public int[][] Books { get; set; }
        public bool[] Cpu { get; set; }
        public bool[] Live { get; set; }
        public int Turn { get; set; }
        public int DeckCount { get; set; }
        public bool Over { get; set; }
        public string Banner { get; set; }
        public string[] Log { get; set; }

        // animation event
        public int[] Cards { get; set; }
        public int From { get; set; }          // seat index, or -1 for the pond
        public int To { get; set; }            // seat index
        public bool ToBook { get; set; }
        public bool FaceUp { get; set; }       // per-recipient: never leaks hidden cards
    }

    /// <summary>
    /// One TCP connection, newline-delimited JSON. Reads on a background task
    /// and marshals every message onto the UI thread, so neither the game
    /// logic nor the renderer ever thinks about synchronization.
    /// </summary>
    public sealed class Link
    {
        readonly TcpClient tcp;
        readonly StreamReader reader;
        readonly StreamWriter writer;
        readonly object sendLock = new object();
        volatile bool dead;

        public object Tag;                     // host stashes the Seat here
        public bool Dead => dead;

        public event Action<Link, Msg> Received;
        public event Action<Link, string> Dropped;

        static readonly JsonSerializerOptions Json =
            new JsonSerializerOptions { WriteIndented = false };  // framing depends on this

        public Link(TcpClient client)
        {
            tcp = client;
            tcp.NoDelay = true;                // card games are latency-bound, not throughput-bound
            var s = tcp.GetStream();
            reader = new StreamReader(s, new UTF8Encoding(false));
            writer = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true };
        }

        public void Start() => Task.Run(ReadLoop);

        async Task ReadLoop()
        {
            try
            {
                while (!dead)
                {
                    string line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    if (line.Length == 0) continue;

                    Msg m;
                    try { m = JsonSerializer.Deserialize<Msg>(line, Json); }
                    catch (JsonException) { continue; }   // skip garbage, don't kill the link
                    if (m == null) continue;

                    var copy = m;
                    Dispatcher.UIThread.Post(() => Received?.Invoke(this, copy));
                }
                Die("Connection closed.");
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
            {
                Die("Connection lost.");
            }
        }

        public void Send(Msg m)
        {
            if (dead) return;
            try
            {
                string line = JsonSerializer.Serialize(m, Json);
                lock (sendLock) writer.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
            {
                Die("Connection lost.");
            }
        }

        void Die(string why)
        {
            if (dead) return;
            dead = true;
            Dispatcher.UIThread.Post(() => Dropped?.Invoke(this, why));
        }

        public void Close()
        {
            dead = true;
            try { tcp.Close(); } catch { }
        }
    }

    public static class Net
    {
        public const int TcpPort = 34780;
        public const int UdpPort = 34781;
        public const string Probe = "GOFISH?";
        public const string Reply = "GOFISH!";

        public static List<string> LocalAddresses()
        {
            var list = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                            list.Add(ua.Address.ToString());
                }
            }
            catch { }
            if (list.Count == 0) list.Add("127.0.0.1");
            return list;
        }
    }

    // =====================================================================
    //  CPU BRAINS
    //  Three genuinely different algorithms, not one algorithm with a
    //  difficulty multiplier. Every brain at the table observes every
    //  public ask, including asks between two other players.
    // =====================================================================

    public sealed class Brain
    {
        public SeatKind Level;
        readonly Random rng;
        readonly HashSet<long> has = new HashSet<long>();      // seat is KNOWN to hold rank
        readonly HashSet<long> hasNot = new HashSet<long>();   // seat is PROVEN not to hold rank
        readonly HashSet<int> revealed = new HashSet<int>();   // ranks I have already asked for publicly
        readonly int me;

        public Brain(SeatKind level, int seat, int seed)
        {
            Level = level; me = seat; rng = new Random(seed);
        }

        static long K(int seat, int rank) => (long)seat * 100 + rank;

        /// <summary>Fed by the host after every ask, for every brain at the table.</summary>
        public void Observe(int asker, int rank, int target, bool hit)
        {
            // An ask is a public admission that the asker holds that rank.
            has.Add(K(asker, rank));
            hasNot.Remove(K(asker, rank));
            if (asker == me) revealed.Add(rank);

            if (hit)
            {
                // Target surrendered every copy, so it is now provably empty of it.
                has.Remove(K(target, rank));
                hasNot.Add(K(target, rank));
            }
            else
            {
                hasNot.Add(K(target, rank));
                has.Remove(K(target, rank));
            }
        }

        /// <summary>A rank left the game; all knowledge about it is now worthless.</summary>
        public void Booked(int rank)
        {
            for (int s = 0; s < 4; s++) { has.Remove(K(s, rank)); hasNot.Remove(K(s, rank)); }
            revealed.Remove(rank);
        }

        public void Forget() { has.Clear(); hasNot.Clear(); revealed.Clear(); }

        public void SawDraw(int seat)
        {
            // A card from the pond is unknowable, so every negative about that
            // seat is now stale. Hard play is the only tier honest enough to care.
            if (Level != SeatKind.CpuHard) return;
            for (int r = 1; r <= 13; r++) hasNot.Remove(K(seat, r));
        }

        public (int rank, int target) Choose(List<Card> hand, List<int> targets, int[] counts)
        {
            var ranks = hand.Select(c => c.Rank).Distinct().ToList();
            if (ranks.Count == 0 || targets.Count == 0) return (0, -1);

            switch (Level)
            {
                case SeatKind.CpuEasy: return Easy(ranks, targets);
                case SeatKind.CpuNormal: return Normal(hand, ranks, targets);
                default: return Hard(hand, ranks, targets, counts);
            }
        }

        // -- Easy: no memory whatsoever. Will cheerfully ask you for a rank it
        //    just watched you hand over.
        (int, int) Easy(List<int> ranks, List<int> targets) =>
            (ranks[rng.Next(ranks.Count)], targets[rng.Next(targets.Count)]);

        // -- Normal: positive knowledge only. Cashes in a guaranteed hit when it
        //    has one, otherwise leans on whichever rank it holds the most of.
        (int, int) Normal(List<Card> hand, List<int> ranks, List<int> targets)
        {
            foreach (int r in ranks)
                foreach (int t in targets)
                    if (has.Contains(K(t, r))) return (r, t);

            int best = ranks
                .OrderByDescending(r => hand.Count(c => c.Rank == r))
                .ThenBy(_ => rng.Next())
                .First();
            return (best, targets[rng.Next(targets.Count)]);
        }

        // -- Hard: positive AND negative knowledge, plus information hygiene.
        //    Never burns a turn on a target it has already ruled out, and when
        //    it has no certain hit it prefers re-asking ranks it has already
        //    revealed, so the ask leaks nothing new to the table.
        (int, int) Hard(List<Card> hand, List<int> ranks, List<int> targets, int[] counts)
        {
            int bestR = -1, bestT = -1;
            double bestScore = double.NegativeInfinity;

            foreach (int r in ranks)
            {
                int held = hand.Count(c => c.Rank == r);
                foreach (int t in targets)
                {
                    double score = 0;
                    if (has.Contains(K(t, r))) score += 1000;       // certainty dominates
                    if (hasNot.Contains(K(t, r))) score -= 500;     // proven dead end
                    score += held * 12;                             // 3-of-a-kind is one card from a book
                    if (revealed.Contains(r)) score += 7;           // costs no new information
                    score += (t < counts.Length ? counts[t] : 0) * 0.8; // fat hands are likelier to hold it
                    score += rng.NextDouble();                      // break ties without being predictable

                    if (score > bestScore) { bestScore = score; bestR = r; bestT = t; }
                }
            }

            if (bestR < 0) return Easy(ranks, targets);
            return (bestR, bestT);
        }
    }

    // =====================================================================
    //  SEATS
    // =====================================================================

    public sealed class Seat
    {
        public int Index;
        public string Name;
        public SeatKind Kind;
        public Link Link;                     // null for local human and CPUs
        public Brain Brain;                   // null for humans
        public bool Connected = true;
        public List<Card> Hand = new List<Card>();
        public List<int> BookList = new List<int>();

        public bool IsCpu => Kind == SeatKind.CpuEasy || Kind == SeatKind.CpuNormal || Kind == SeatKind.CpuHard;
        public bool IsNetwork => Link != null;
    }

    // =====================================================================
    //  HOST  —  owns all authoritative state
    //  Everything below runs on the UI thread (Link posts inbound messages
    //  there), so there is no locking anywhere in the rule engine.
    // =====================================================================

    public sealed class HostGame
    {
        public readonly SeatKind[] Slots = { SeatKind.Human, SeatKind.CpuNormal, SeatKind.Closed, SeatKind.Closed };
        public readonly List<Seat> Seats = new List<Seat>();
        public bool Started { get; private set; }
        public bool Over { get; private set; }
        public string HostName = "Host";

        readonly List<Card> deck = new List<Card>();
        readonly List<string> log = new List<string>();
        readonly List<Link> waiting = new List<Link>();
        readonly Random rng = new Random();

        TcpListener listener;
        UdpClient discovery;
        DispatcherTimer cpuTimer;
        int turn;
        string banner = "";
        bool booked13;

        public event Action Changed;                  // lobby / UI refresh
        public Action<Msg> ToLocal;                   // delivers seat 0's messages to the local view

        // -------------------------------------------------------- listen --
        public bool Listen(out string error)
        {
            error = null;
            try
            {
                listener = new TcpListener(IPAddress.Any, Net.TcpPort);
                listener.Start();
                Task.Run(AcceptLoop);
                StartDiscoveryResponder();
                return true;
            }
            catch (SocketException ex) { error = ex.Message; return false; }
        }

        async Task AcceptLoop()
        {
            while (true)
            {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch { return; }

                var link = new Link(c);
                link.Received += OnMsg;
                link.Dropped += OnDropped;
                Dispatcher.UIThread.Post(() =>
                {
                    if (Started) { link.Send(new Msg { T = "kick", Banner = "Game already in progress." }); link.Close(); return; }
                    if (waiting.Count >= OpenSlotCount()) { link.Send(new Msg { T = "kick", Banner = "No open seats." }); link.Close(); return; }
                    waiting.Add(link);
                    Changed?.Invoke();
                    PushLobby();
                });
                link.Start();
            }
        }

        void StartDiscoveryResponder()
        {
            try
            {
                discovery = new UdpClient(new IPEndPoint(IPAddress.Any, Net.UdpPort));
                Task.Run(async () =>
                {
                    while (true)
                    {
                        UdpReceiveResult r;
                        try { r = await discovery.ReceiveAsync().ConfigureAwait(false); }
                        catch { return; }
                        if (Encoding.UTF8.GetString(r.Buffer) != Net.Probe) continue;
                        var reply = Encoding.UTF8.GetBytes(Net.Reply);
                        try { await discovery.SendAsync(reply, reply.Length, r.RemoteEndPoint).ConfigureAwait(false); } catch { }
                    }
                });
            }
            catch { /* discovery is a nicety; a taken port must not stop hosting */ }
        }

        public int OpenSlotCount() => Slots.Count(s => s == SeatKind.Open);
        public int WaitingCount() => waiting.Count;

        public void CycleSlot(int i)
        {
            if (Started || i < 1 || i > 3) return;
            Slots[i] = Slots[i] switch
            {
                SeatKind.Closed => SeatKind.Open,
                SeatKind.Open => SeatKind.CpuEasy,
                SeatKind.CpuEasy => SeatKind.CpuNormal,
                SeatKind.CpuNormal => SeatKind.CpuHard,
                _ => SeatKind.Closed
            };
            // Over-subscribed Open seats have to release their pending clients.
            while (waiting.Count > OpenSlotCount())
            {
                var l = waiting[waiting.Count - 1];
                waiting.RemoveAt(waiting.Count - 1);
                l.Send(new Msg { T = "kick", Banner = "Seat closed by host." });
                l.Close();
            }
            Changed?.Invoke();
            PushLobby();
        }

        public string LobbyStatus()
        {
            int filled = 1 + Slots.Skip(1).Count(s => s != SeatKind.Closed && s != SeatKind.Open)
                           + Math.Min(waiting.Count, OpenSlotCount());
            int need = OpenSlotCount() - Math.Min(waiting.Count, OpenSlotCount());
            if (filled < 2) return "Need at least one opponent.";
            if (need > 0) return "Waiting for " + need + " more player" + (need == 1 ? "" : "s") + "...";
            return "Ready — " + filled + " players.";
        }

        public bool CanStart()
        {
            int filled = 1 + Slots.Skip(1).Count(s => s != SeatKind.Closed && s != SeatKind.Open)
                           + Math.Min(waiting.Count, OpenSlotCount());
            return !Started && filled >= 2 && waiting.Count >= OpenSlotCount();
        }

        // --------------------------------------------------------- start --
        public void Start()
        {
            if (!CanStart()) return;

            // Compact the roster: closed seats vanish, so the host does not have
            // to fill slots in order for the indices to come out contiguous.
            Seats.Clear();
            int wi = 0, seed = rng.Next();
            var host = new Seat { Index = 0, Name = HostName, Kind = SeatKind.Human };
            Seats.Add(host);

            for (int i = 1; i <= 3; i++)
            {
                var k = Slots[i];
                if (k == SeatKind.Closed) continue;

                if (k == SeatKind.Open)
                {
                    if (wi >= waiting.Count) continue;
                    var link = waiting[wi++];
                    var s = new Seat { Index = Seats.Count, Name = "Player " + (Seats.Count + 1), Kind = SeatKind.Human, Link = link };
                    link.Tag = s;
                    Seats.Add(s);
                }
                else
                {
                    string label = k == SeatKind.CpuEasy ? "CPU (Easy)" : k == SeatKind.CpuNormal ? "CPU (Normal)" : "CPU (Hard)";
                    var s = new Seat { Index = Seats.Count, Name = label, Kind = k };
                    s.Brain = new Brain(k, s.Index, seed + s.Index * 7919);
                    Seats.Add(s);
                }
            }

            // Anyone still pending had their seat evaporate during compaction.
            for (; wi < waiting.Count; wi++)
            {
                waiting[wi].Send(new Msg { T = "kick", Banner = "Seat unavailable." });
                waiting[wi].Close();
            }
            waiting.Clear();

            Started = true;
            NewGame();
            Changed?.Invoke();
        }

        public void NewGame()
        {
            deck.Clear();
            log.Clear();
            Over = false; booked13 = false;

            for (int r = 1; r <= 13; r++)
                for (int s = 0; s < 4; s++)
                    deck.Add(new Card(r, (Suit)s));

            for (int i = deck.Count - 1; i > 0; i--)          // Fisher-Yates
            {
                int j = rng.Next(i + 1);
                (deck[i], deck[j]) = (deck[j], deck[i]);
            }

            int per = Seats.Count <= 2 ? 7 : 5;               // standard: fewer cards with more players
            foreach (var s in Seats)
            {
                s.Hand.Clear(); s.BookList.Clear();
                s.Brain?.Forget();
                for (int i = 0; i < per; i++) s.Hand.Add(Pop());
                Sort(s);
            }

            turn = rng.Next(Seats.Count);
            Log("Dealt " + per + " cards each. " + Seats.Count + " players.");
            Log(Seats[turn].Name + " goes first.");

            foreach (var s in Seats) TakeBooks(s);            // a dealt book is still a book

            banner = Seats[turn].Name + " to act";
            Broadcast();
            Advance();
        }

        Card Pop()
        {
            if (deck.Count == 0) return null;
            var c = deck[deck.Count - 1];
            deck.RemoveAt(deck.Count - 1);
            return c;
        }

        static void Sort(Seat s) =>
            s.Hand = s.Hand.OrderBy(c => c.Rank).ThenBy(c => (int)c.Suit).ToList();

        void Log(string line)
        {
            log.Add(line);
            if (log.Count > 200) log.RemoveAt(0);
        }

        // ------------------------------------------------------ messages --
        void OnMsg(Link link, Msg m)
        {
            if (m.T == "hello")
            {
                string n = string.IsNullOrWhiteSpace(m.Name) ? "Player" : m.Name.Trim();
                if (n.Length > 14) n = n.Substring(0, 14);
                if (link.Tag is Seat seat) { seat.Name = n; Broadcast(); }
                else { link.Tag = n; Changed?.Invoke(); PushLobby(); }
                return;
            }

            if (m.T == "ask" && link.Tag is Seat asker)
            {
                ResolveAsk(asker.Index, m.Rank, m.Target);
                return;
            }
        }

        void OnDropped(Link link, string why)
        {
            if (link.Tag is Seat seat)
            {
                // Promote rather than collapse. A three-player game should not
                // end because one person's WiFi hiccuped.
                if (!Over && seat.Connected)
                {
                    seat.Connected = false;
                    seat.Link = null;
                    seat.Kind = SeatKind.CpuNormal;
                    seat.Brain = new Brain(SeatKind.CpuNormal, seat.Index, rng.Next());
                    Log(seat.Name + " disconnected — taken over by CPU (Normal).");
                    seat.Name = seat.Name + " [CPU]";
                    Broadcast();
                    Advance();
                }
            }
            else
            {
                waiting.Remove(link);
                Changed?.Invoke();
                PushLobby();
            }
        }

        // ---------------------------------------------------------- rules --
        public List<int> LegalTargets(int seatIdx) =>
            Seats.Where(s => s.Index != seatIdx && s.Hand.Count > 0).Select(s => s.Index).ToList();

        public void ResolveAsk(int askerIdx, int rank, int targetIdx)
        {
            if (!Started || Over) return;
            if (askerIdx != turn) return;                                  // not your turn
            if (askerIdx == targetIdx) return;
            if (targetIdx < 0 || targetIdx >= Seats.Count) return;
            if (rank < 1 || rank > 13) return;

            var asker = Seats[askerIdx];
            var target = Seats[targetIdx];
            if (!asker.Hand.Any(c => c.Rank == rank)) return;              // must hold the rank you ask for
            if (target.Hand.Count == 0) return;

            var taken = target.Hand.Where(c => c.Rank == rank).ToList();
            bool hit = taken.Count > 0;

            Log(asker.Name + " asks " + target.Name + " for " + Card.WordOf(rank) + ".");
            foreach (var s in Seats) s.Brain?.Observe(askerIdx, rank, targetIdx, hit);

            if (hit)
            {
                foreach (var c in taken) target.Hand.Remove(c);
                asker.Hand.AddRange(taken);
                Sort(asker);
                Log("  " + target.Name + " hands over " + taken.Count + ".");
                EmitMove(taken.Select(c => c.Id).ToArray(), targetIdx, askerIdx, false, _ => true);

                banner = asker.Name + " got " + Card.WordOf(rank) + " — goes again";
                TakeBooks(asker);
                Restock(target);
                if (!CheckOver()) { Broadcast(); Advance(); }
                return;
            }

            // Miss: go fish.
            Log("  Go Fish!");
            var drawn = Pop();
            if (drawn != null)
            {
                asker.Hand.Add(drawn);
                Sort(asker);
                // Face up to the drawer alone. The event is emitted per recipient,
                // so the wire itself never carries a card the client may not see.
                EmitMove(new[] { drawn.Id }, -1, askerIdx, false, r => r == askerIdx);
                foreach (var s in Seats) s.Brain?.SawDraw(askerIdx);

                if (drawn.Rank == rank)
                {
                    Log("  " + asker.Name + " drew the " + Card.LabelOf(rank) + " — goes again.");
                    banner = asker.Name + " fished it out — goes again";
                    TakeBooks(asker);
                    if (!CheckOver()) { Broadcast(); Advance(); }
                    return;
                }
                TakeBooks(asker);
            }
            else
            {
                Log("  The pond is empty.");
            }

            if (CheckOver()) return;
            NextTurn();
            Broadcast();
            Advance();
        }

        void TakeBooks(Seat s)
        {
            bool any = true;
            while (any)
            {
                any = false;
                for (int r = 1; r <= 13; r++)
                {
                    var four = s.Hand.Where(c => c.Rank == r).ToList();
                    if (four.Count < 4) continue;
                    foreach (var c in four) s.Hand.Remove(c);
                    s.BookList.Add(r);
                    Log("  " + s.Name + " books the " + Card.WordOf(r) + "!");
                    EmitMove(four.Select(c => c.Id).ToArray(), s.Index, s.Index, true, _ => true);
                    foreach (var b in Seats) b.Brain?.Booked(r);
                    any = true;
                }
            }
            Restock(s);
        }

        void Restock(Seat s)
        {
            if (s.Hand.Count > 0 || deck.Count == 0) return;
            int n = Math.Min(5, deck.Count);
            var ids = new List<int>();
            for (int i = 0; i < n; i++) { var c = Pop(); s.Hand.Add(c); ids.Add(c.Id); }
            Sort(s);
            Log("  " + s.Name + " restocks " + n + " from the pond.");
            EmitMove(ids.ToArray(), -1, s.Index, false, r => r == s.Index);
            TakeBooks(s);
        }

        void NextTurn()
        {
            for (int i = 1; i <= Seats.Count; i++)
            {
                int cand = (turn + i) % Seats.Count;
                Restock(Seats[cand]);
                if (Seats[cand].Hand.Count > 0) { turn = cand; banner = Seats[cand].Name + " to act"; return; }
            }
            // Nobody holds a card: the game is finished.
            Over = true;
        }

        bool CheckOver()
        {
            int books = Seats.Sum(s => s.BookList.Count);
            if (books >= 13) { booked13 = true; Over = true; }
            else if (LegalTargets(turn).Count == 0 && deck.Count == 0) Over = true;
            else if (Seats.All(s => s.Hand.Count == 0) && deck.Count == 0) Over = true;

            if (!Over) return false;

            int top = Seats.Max(s => s.BookList.Count);
            var winners = Seats.Where(s => s.BookList.Count == top).ToList();
            banner = winners.Count == 1
                ? winners[0].Name + " wins with " + top + " books"
                : "Tie at " + top + " books: " + string.Join(", ", winners.Select(w => w.Name));
            Log("Game over. " + banner + (booked13 ? "" : " (pond and hands exhausted)"));
            Broadcast();
            return true;
        }

        // ------------------------------------------------------- CPU turn --
        void Advance()
        {
            if (!Started || Over) return;
            var s = Seats[turn];
            if (!s.IsCpu) return;                       // human seats simply wait for an "ask"

            cpuTimer?.Stop();
            cpuTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            cpuTimer.Tick += (_, __) =>
            {
                cpuTimer.Stop();
                if (Over || !Started) return;
                var cur = Seats[turn];
                if (!cur.IsCpu) return;

                var targets = LegalTargets(cur.Index);
                if (targets.Count == 0 || cur.Hand.Count == 0) { NextTurn(); Broadcast(); Advance(); return; }

                var counts = Seats.Select(x => x.Hand.Count).ToArray();
                var (rank, target) = cur.Brain.Choose(cur.Hand, targets, counts);
                if (rank <= 0 || target < 0) { NextTurn(); Broadcast(); Advance(); return; }
                ResolveAsk(cur.Index, rank, target);
            };
            cpuTimer.Start();
        }

        // ------------------------------------------------------ transport --
        void EmitMove(int[] ids, int from, int to, bool toBook, Func<int, bool> faceUpFor)
        {
            foreach (var s in Seats)
            {
                var m = new Msg
                {
                    T = "ev", Cards = ids, From = from, To = to,
                    ToBook = toBook, FaceUp = faceUpFor(s.Index)
                };
                SendTo(s, m);
            }
        }

        void SendTo(Seat s, Msg m)
        {
            if (s.Index == 0) ToLocal?.Invoke(m);
            else s.Link?.Send(m);
        }

        public void Broadcast()
        {
            var names = Seats.Select(s => s.Name).ToArray();
            var counts = Seats.Select(s => s.Hand.Count).ToArray();
            var books = Seats.Select(s => s.BookList.ToArray()).ToArray();
            var cpu = Seats.Select(s => s.IsCpu).ToArray();
            var live = Seats.Select(s => s.Connected).ToArray();
            var tail = log.Skip(Math.Max(0, log.Count - 24)).ToArray();

            foreach (var s in Seats)
            {
                SendTo(s, new Msg
                {
                    T = "state",
                    You = s.Index,
                    Hand = s.Hand.Select(c => c.Id).ToArray(),   // only ever the recipient's own cards
                    Names = names, Counts = counts, Books = books,
                    Cpu = cpu, Live = live,
                    Turn = turn, DeckCount = deck.Count,
                    Over = Over, Banner = banner, Log = tail
                });
            }
        }

        void PushLobby()
        {
            var names = new List<string>();
            var kinds = new List<string>();
            names.Add(HostName); kinds.Add("Host");
            int wi = 0;
            for (int i = 1; i <= 3; i++)
            {
                var k = Slots[i];
                if (k == SeatKind.Closed) continue;
                if (k == SeatKind.Open)
                {
                    if (wi < waiting.Count) { names.Add(waiting[wi].Tag as string ?? "Player"); kinds.Add("Human"); wi++; }
                    else { names.Add("(open)"); kinds.Add("Open"); }
                }
                else
                {
                    names.Add(k == SeatKind.CpuEasy ? "CPU (Easy)" : k == SeatKind.CpuNormal ? "CPU (Normal)" : "CPU (Hard)");
                    kinds.Add("Cpu");
                }
            }
            var m = new Msg { T = "lobby", SeatNames = names.ToArray(), SeatKinds = kinds.ToArray() };
            foreach (var l in waiting) l.Send(m);
        }

        public void Shutdown()
        {
            cpuTimer?.Stop();
            try { listener?.Stop(); } catch { }
            try { discovery?.Close(); } catch { }
            foreach (var l in waiting) l.Close();
            foreach (var s in Seats) s.Link?.Close();
        }
    }

    // =====================================================================
    //  CLIENT
    // =====================================================================

    public sealed class ClientGame
    {
        Link link;
        public event Action<Msg> Message;
        public event Action<string> Dropped;

        public async Task<string> Connect(string host, string name)
        {
            try
            {
                var tcp = new TcpClient();
                var connect = tcp.ConnectAsync(host, Net.TcpPort);
                var timeout = Task.Delay(6000);
                if (await Task.WhenAny(connect, timeout).ConfigureAwait(false) == timeout)
                {
                    tcp.Close();
                    return "Timed out connecting to " + host + ".";
                }
                await connect.ConfigureAwait(false);

                link = new Link(tcp);
                link.Received += (_, m) => Message?.Invoke(m);
                link.Dropped += (_, why) => Dropped?.Invoke(why);
                link.Start();
                link.Send(new Msg { T = "hello", Name = name });
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        public void Ask(int rank, int target) => link?.Send(new Msg { T = "ask", Rank = rank, Target = target });
        public void Close() => link?.Close();

        /// <summary>UDP broadcast probe; returns the first host that answers.</summary>
        public static async Task<string> Discover()
        {
            try
            {
                using (var udp = new UdpClient())
                {
                    udp.EnableBroadcast = true;
                    var probe = Encoding.UTF8.GetBytes(Net.Probe);
                    await udp.SendAsync(probe, probe.Length, new IPEndPoint(IPAddress.Broadcast, Net.UdpPort)).ConfigureAwait(false);

                    var recv = udp.ReceiveAsync();
                    if (await Task.WhenAny(recv, Task.Delay(1500)).ConfigureAwait(false) != recv) return null;
                    var r = await recv.ConfigureAwait(false);
                    if (Encoding.UTF8.GetString(r.Buffer) != Net.Reply) return null;
                    return r.RemoteEndPoint.Address.ToString();
                }
            }
            catch { return null; }
        }
    }

    // =====================================================================
    //  VIEW
    // =====================================================================

    sealed class Flight
    {
        public Card Card;
        public Point From, To;
        public bool FaceUp;
        public double Stagger;
    }

    sealed class SeatBox
    {
        public Rect Panel;
        public Point Center;      // where flights land
        public Point BookAt;
    }

    public enum Screen { Menu, Lobby, Joining, ClientWait, Game, Dead }

    public sealed class GameView : Control
    {
        // ---- geometry ----
        const double W = 1240, H = 800;
        const double CardW = 92, CardH = 132;
        const double MiniW = 30, MiniH = 44;
        const double BookW = 40, BookH = 58;
        const double LogX = 986, LogW = 240;
        const double TableCx = 486;

        static readonly Rect DeckRect = new Rect(440, 296, CardW, CardH);
        static readonly Rect HostBtn = new Rect(TableCx - 300, 300, 280, 70);
        static readonly Rect JoinBtn = new Rect(TableCx + 20, 300, 280, 70);
        static readonly Rect IpBox = new Rect(TableCx - 300, 400, 420, 52);
        static readonly Rect FindBtn = new Rect(TableCx + 140, 400, 160, 52);
        static readonly Rect GoBtn = new Rect(TableCx - 120, 480, 240, 56);
        static readonly Rect StartBtn = new Rect(TableCx - 130, 610, 260, 60);
        static readonly Rect BackBtn = new Rect(30, 730, 150, 48);
        static readonly Rect RestartBtn = new Rect(TableCx - 100, 448, 200, 48);

        // ---- palette ----
        static readonly IBrush Felt = new SolidColorBrush(Color.FromRgb(18, 62, 44));
        static readonly IBrush FeltLite = new SolidColorBrush(Color.FromRgb(28, 88, 62));
        static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(24, 26, 32));
        static readonly IBrush Red = new SolidColorBrush(Color.FromRgb(198, 40, 44));
        static readonly IBrush Paper = new SolidColorBrush(Color.FromRgb(250, 249, 245));
        static readonly IBrush BackA = new SolidColorBrush(Color.FromRgb(28, 58, 122));
        static readonly IBrush BackB = new SolidColorBrush(Color.FromRgb(52, 96, 178));
        static readonly IBrush Panel = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
        static readonly IBrush Panel2 = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
        static readonly IBrush Gold = new SolidColorBrush(Color.FromRgb(240, 196, 88));
        static readonly IBrush White = Brushes.White;
        static readonly IBrush Faint = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
        static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(200, 8, 26, 20));
        static readonly IPen Edge = new Pen(new SolidColorBrush(Color.FromRgb(38, 40, 46)), 1.4);
        static readonly IPen GoldPen = new Pen(Gold, 2.5);
        static readonly IPen HotPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 226, 130)), 3);
        static readonly IPen ThinPen = new Pen(Faint, 1);

        static readonly Typeface Face = new Typeface(FontFamily.Default);
        static readonly Typeface Bold = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

        // ---- mode ----
        Screen screen = Screen.Menu;
        string error = "";
        string ip = "127.0.0.1";
        bool ipFocus;
        string myName = Environment.UserName ?? "Player";

        HostGame host;
        ClientGame client;

        // ---- snapshot (identical code path for host and client) ----
        int you, turn, deckCount;
        List<Card> hand = new List<Card>();
        string[] names = new string[0];
        int[] counts = new int[0];
        int[][] books = new int[0][];
        bool[] isCpu = new bool[0];
        bool[] live = new bool[0];
        bool over;
        string banner = "";
        string[] logLines = new string[0];
        bool haveState;

        string[] lobbyNames = new string[0], lobbyKinds = new string[0];

        // ---- interaction ----
        int hovered = -1, selRank = -1, hoveredSeat = -1;

        // ---- animation ----
        readonly Queue<Msg> inbox = new Queue<Msg>();
        List<Flight> flying;
        double flyT;
        DispatcherTimer clock;
        SeatBox[] boxes = new SeatBox[0];

        public GameView()
        {
            Width = W; Height = H;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Arrow);

            clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            clock.Tick += (_, __) => Tick();
            clock.Start();
        }

        // ================================================= message intake ==
        void Deliver(Msg m)
        {
            if (m == null) return;
            if (m.T == "kick") { Fail(string.IsNullOrEmpty(m.Banner) ? "Rejected by host." : m.Banner); return; }
            if (m.T == "lobby") { lobbyNames = m.SeatNames ?? new string[0]; lobbyKinds = m.SeatKinds ?? new string[0]; InvalidateVisual(); return; }
            inbox.Enqueue(m);
            Pump();
        }

        /// <summary>
        /// Flights and snapshots arrive as separate messages, so the queue only
        /// drains while nothing is mid-air. Otherwise the snapshot showing a card
        /// in its new home would land before the animation that moved it there.
        /// </summary>
        void Pump()
        {
            while (flying == null && inbox.Count > 0)
            {
                var m = inbox.Dequeue();
                if (m.T == "state") { ApplyState(m); continue; }
                if (m.T == "ev") { StartFlight(m); return; }
            }
            InvalidateVisual();
        }

        void ApplyState(Msg m)
        {
            you = m.You;
            hand = (m.Hand ?? new int[0]).Select(Card.FromId).ToList();
            names = m.Names ?? new string[0];
            counts = m.Counts ?? new int[0];
            books = m.Books ?? new int[0][];
            isCpu = m.Cpu ?? new bool[0];
            live = m.Live ?? new bool[0];
            turn = m.Turn; deckCount = m.DeckCount;
            over = m.Over; banner = m.Banner ?? "";
            logLines = m.Log ?? new string[0];
            haveState = true;
            if (screen != Screen.Game) { screen = Screen.Game; }
            Layout();
            if (selRank >= 0 && !hand.Any(c => c.Rank == selRank)) selRank = -1;
            InvalidateVisual();
        }

        void StartFlight(Msg m)
        {
            if (!haveState) { InvalidateVisual(); return; }
            var ids = m.Cards ?? new int[0];
            if (ids.Length == 0) { Pump(); return; }

            Point from = m.From < 0 ? DeckRect.Center : CenterOf(m.From);
            Point to = m.ToBook ? BookOf(m.To) : CenterOf(m.To);

            flying = new List<Flight>();
            for (int i = 0; i < ids.Length; i++)
                flying.Add(new Flight
                {
                    Card = Card.FromId(ids[i]),
                    From = from, To = to,
                    FaceUp = m.FaceUp,
                    Stagger = i * 9
                });
            flyT = 0;
            InvalidateVisual();
        }

        void Tick()
        {
            if (flying == null) return;
            flyT += 16.0 / 420.0;
            if (flyT >= 1) { flying = null; flyT = 0; Pump(); }
            InvalidateVisual();
        }

        void Fail(string why)
        {
            error = why;
            screen = Screen.Dead;
            host?.Shutdown(); host = null;
            client?.Close(); client = null;
            InvalidateVisual();
        }

        void ToMenu()
        {
            host?.Shutdown(); host = null;
            client?.Close(); client = null;
            inbox.Clear(); flying = null; haveState = false;
            selRank = -1; hovered = -1; hoveredSeat = -1; error = "";
            screen = Screen.Menu;
            InvalidateVisual();
        }

        // ======================================================== layout ==
        // Seats are laid out relative to YOU: you are always at the bottom and
        // opponents fill left/top/right in turn order, so the table reads the
        // same on every machine even though each has a different seat index.
        void Layout()
        {
            int n = Math.Max(1, names.Length);
            boxes = new SeatBox[n];

            boxes[you] = new SeatBox
            {
                Panel = new Rect(200, 508, 580, 40),
                Center = new Point(TableCx, 642 + CardH / 2),
                BookAt = new Point(236, 536)
            };

            var slots = new List<Rect>();
            int opp = n - 1;
            if (opp == 1) slots.Add(new Rect(330, 18, 310, 130));
            else if (opp == 2) { slots.Add(new Rect(24, 150, 290, 130)); slots.Add(new Rect(660, 150, 290, 130)); }
            else { slots.Add(new Rect(24, 150, 290, 130)); slots.Add(new Rect(330, 18, 310, 130)); slots.Add(new Rect(660, 150, 290, 130)); }

            for (int i = 1; i < n; i++)
            {
                int idx = (you + i) % n;
                var r = slots[Math.Min(i - 1, slots.Count - 1)];
                boxes[idx] = new SeatBox
                {
                    Panel = r,
                    Center = new Point(r.Center.X, r.Y + 52),
                    BookAt = new Point(r.X + 12, r.Bottom - 34)
                };
            }
        }

        Point CenterOf(int seat) =>
            seat >= 0 && seat < boxes.Length && boxes[seat] != null ? boxes[seat].Center : DeckRect.Center;

        Point BookOf(int seat) =>
            seat >= 0 && seat < boxes.Length && boxes[seat] != null ? boxes[seat].BookAt : DeckRect.Center;

        // ========================================================= input ==
        protected override void OnPointerMoved(PointerEventArgs e)
        {
            var p = e.GetPosition(this);
            int h = screen == Screen.Game ? HitHand(p) : -1;
            int s = screen == Screen.Game ? HitSeat(p) : -1;
            if (h != hovered || s != hoveredSeat) { hovered = h; hoveredSeat = s; InvalidateVisual(); }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            var p = e.GetPosition(this);
            Focus();

            switch (screen)
            {
                case Screen.Menu: MenuClick(p); return;
                case Screen.Lobby: LobbyClick(p); return;
                case Screen.Dead: if (BackBtn.Contains(p) || GoBtn.Contains(p)) ToMenu(); return;
                case Screen.ClientWait: if (BackBtn.Contains(p)) ToMenu(); return;
                case Screen.Game: GameClick(p); return;
            }
        }

        void MenuClick(Point p)
        {
            if (HostBtn.Contains(p)) { BeginHost(); return; }
            if (IpBox.Contains(p)) { ipFocus = true; InvalidateVisual(); return; }
            ipFocus = false;

            if (FindBtn.Contains(p)) { Find(); return; }
            if (JoinBtn.Contains(p) || GoBtn.Contains(p)) { BeginJoin(); return; }
            InvalidateVisual();
        }

        void LobbyClick(Point p)
        {
            if (host == null) return;
            for (int i = 1; i <= 3; i++)
                if (SlotRect(i).Contains(p)) { host.CycleSlot(i); return; }

            if (StartBtn.Contains(p) && host.CanStart()) { host.Start(); return; }
            if (BackBtn.Contains(p)) { ToMenu(); return; }
        }

        void GameClick(Point p)
        {
            if (over)
            {
                if (RestartBtn.Contains(p) && host != null) { selRank = -1; host.NewGame(); }
                return;
            }
            if (turn != you || flying != null) return;

            int h = HitHand(p);
            if (h >= 0)
            {
                int rank = hand[h].Rank;
                selRank = selRank == rank ? -1 : rank;
                var t = Targets();
                if (selRank > 0 && t.Count == 1) Ask(selRank, t[0]);   // only one opponent: no target step
                InvalidateVisual();
                return;
            }

            int seat = HitSeat(p);
            if (seat >= 0 && seat != you && counts[seat] > 0)
            {
                if (selRank > 0) Ask(selRank, seat);
                InvalidateVisual();
            }
        }

        void Ask(int rank, int target)
        {
            selRank = -1;
            if (host != null) host.ResolveAsk(you, rank, target);
            else client?.Ask(rank, target);
        }

        List<int> Targets()
        {
            var list = new List<int>();
            for (int i = 0; i < counts.Length; i++)
                if (i != you && counts[i] > 0) list.Add(i);
            return list;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { selRank = -1; InvalidateVisual(); return; }
            if (screen == Screen.Game && over && e.Key == Key.R && host != null) { host.NewGame(); return; }

            if (ipFocus && screen == Screen.Menu)
            {
                if (e.Key == Key.Back && ip.Length > 0) { ip = ip.Substring(0, ip.Length - 1); InvalidateVisual(); }
                else if (e.Key == Key.Enter) BeginJoin();
            }
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            if (!ipFocus || screen != Screen.Menu || string.IsNullOrEmpty(e.Text)) return;
            foreach (char c in e.Text)
                if ((char.IsLetterOrDigit(c) || c == '.' || c == ':' || c == '-') && ip.Length < 40) ip += c;
            InvalidateVisual();
        }

        // ================================================== connect flows ==
        void BeginHost()
        {
            host = new HostGame { HostName = myName };
            host.ToLocal = Deliver;
            host.Changed += InvalidateVisual;
            if (!host.Listen(out string err))
            {
                host = null;
                Fail("Could not open port " + Net.TcpPort + ": " + err + "\nAnother copy may already be hosting.");
                return;
            }
            screen = Screen.Lobby;
            InvalidateVisual();
        }

        async void BeginJoin()
        {
            screen = Screen.Joining; InvalidateVisual();
            client = new ClientGame();
            client.Message += Deliver;
            client.Dropped += Fail;
            string err = await client.Connect(ip.Trim(), myName);
            if (err != null) { client = null; Fail("Could not join " + ip + ".\n" + err); return; }
            screen = Screen.ClientWait;
            InvalidateVisual();
        }

        async void Find()
        {
            error = "Searching the local network...";
            InvalidateVisual();
            string found = await ClientGame.Discover();
            error = found == null ? "No host found on this network." : "";
            if (found != null) ip = found;
            InvalidateVisual();
        }

        // ====================================================== hit tests ==
        List<Rect> HandRects()
        {
            var list = new List<Rect>();
            int n = hand.Count;
            if (n == 0) return list;
            double avail = 900;
            double step = Math.Min(CardW + 8, avail / n);
            double total = step * (n - 1) + CardW;
            double x0 = TableCx - total / 2;
            for (int i = 0; i < n; i++)
            {
                bool lift = i == hovered || hand[i].Rank == selRank;
                list.Add(new Rect(x0 + i * step, 642 - (lift ? 20 : 0), CardW, CardH));
            }
            return list;
        }

        int HitHand(Point p)
        {
            var r = HandRects();
            for (int i = r.Count - 1; i >= 0; i--) if (r[i].Contains(p)) return i;
            return -1;
        }

        int HitSeat(Point p)
        {
            for (int i = 0; i < boxes.Length; i++)
                if (i != you && boxes[i] != null && boxes[i].Panel.Contains(p)) return i;
            return -1;
        }

        static Rect SlotRect(int i) => new Rect(TableCx - 250, 240 + (i - 1) * 76, 500, 62);

        // ======================================================== render ==
        public override void Render(DrawingContext c)
        {
            c.DrawRectangle(Felt, null, new Rect(0, 0, W, H));
            c.DrawEllipse(FeltLite, null, new Point(TableCx, 380), 430, 300);

            switch (screen)
            {
                case Screen.Menu: DrawMenu(c); break;
                case Screen.Lobby: DrawLobby(c); break;
                case Screen.Joining: DrawCenterMsg(c, "Connecting to " + ip + "...", ""); break;
                case Screen.ClientWait: DrawClientWait(c); break;
                case Screen.Dead: DrawCenterMsg(c, "Disconnected", error); Button(c, GoBtn, "BACK TO MENU", true); break;
                case Screen.Game: DrawGame(c); break;
            }
        }

        // ---------------------------------------------------------- menu --
        void DrawMenu(DrawingContext c)
        {
            Text(c, "GO FISH", TableCx, 120, 76, Gold, true, true);
            Text(c, "2 - 4 players  •  network or CPU", TableCx, 206, 21, Faint, false, true);

            Button(c, HostBtn, "HOST A GAME", true);
            Button(c, JoinBtn, "JOIN A GAME", true);

            c.DrawRectangle(Panel2, ipFocus ? GoldPen : Edge, IpBox, 8, 8);
            Text(c, ip + (ipFocus ? "|" : ""), IpBox.X + 14, IpBox.Y + 14, 20, White);
            Text(c, "host address", IpBox.X + 4, IpBox.Y - 24, 14, Faint);
            Button(c, FindBtn, "FIND", true);
            Button(c, GoBtn, "CONNECT", true);

            if (!string.IsNullOrEmpty(error))
                Text(c, error, TableCx, 556, 16, Gold, false, true);

            Text(c, "Your addresses:  " + string.Join("   ", Net.LocalAddresses()), TableCx, 630, 15, Faint, false, true);
            Text(c, "Hosting? Tell the others one of those. Testing solo? Host here, join 127.0.0.1 in a second copy.",
                 TableCx, 660, 14, Faint, false, true);
            Text(c, "Playing as " + myName, TableCx, 700, 15, Faint, false, true);
        }

        // --------------------------------------------------------- lobby --
        void DrawLobby(DrawingContext c)
        {
            Text(c, "LOBBY", TableCx, 90, 52, Gold, true, true);
            Text(c, "Click a seat to change it", TableCx, 168, 17, Faint, false, true);

            var me = new Rect(TableCx - 250, 176, 500, 52);
            c.DrawRectangle(Panel2, Edge, me, 8, 8);
            Text(c, "Seat 1", me.X + 16, me.Y + 16, 17, Faint);
            Text(c, myName + "  (you, host)", me.X + 110, me.Y + 15, 19, White, true);

            for (int i = 1; i <= 3; i++)
            {
                var r = SlotRect(i);
                bool hot = r.Contains(lastPointer);
                var kind = host.Slots[i];
                c.DrawRectangle(Panel2, hot ? HotPen : Edge, r, 8, 8);
                Text(c, "Seat " + (i + 1), r.X + 16, r.Y + 21, 17, Faint);

                string label = kind switch
                {
                    SeatKind.Closed => "Closed",
                    SeatKind.Open => "Open  —  waiting for a network player",
                    SeatKind.CpuEasy => "CPU  —  Easy",
                    SeatKind.CpuNormal => "CPU  —  Normal",
                    _ => "CPU  —  Hard"
                };
                var col = kind == SeatKind.Closed ? Faint : kind == SeatKind.Open ? Gold : White;
                Text(c, label, r.X + 110, r.Y + 19, 19, col, kind != SeatKind.Closed);
            }

            Text(c, host.LobbyStatus(), TableCx, 500, 19, Gold, true, true);
            Text(c, host.WaitingCount() + " connected  /  " + host.OpenSlotCount() + " open seat(s)",
                 TableCx, 532, 15, Faint, false, true);
            Text(c, "Host at:  " + string.Join("   ", Net.LocalAddresses()), TableCx, 562, 15, Faint, false, true);

            Button(c, StartBtn, "START GAME", host.CanStart());
            Button(c, BackBtn, "CANCEL", true);

            Text(c, "Easy forgets everything  •  Normal remembers what you ask for  •  Hard tracks what you DON'T have",
                 TableCx, 700, 14, Faint, false, true);
        }

        void DrawClientWait(DrawingContext c)
        {
            Text(c, "CONNECTED", TableCx, 140, 46, Gold, true, true);
            Text(c, "Waiting for the host to start...", TableCx, 210, 20, Faint, false, true);

            for (int i = 0; i < lobbyNames.Length; i++)
            {
                var r = new Rect(TableCx - 230, 280 + i * 62, 460, 50);
                c.DrawRectangle(Panel2, Edge, r, 8, 8);
                Text(c, "Seat " + (i + 1), r.X + 16, r.Y + 15, 16, Faint);
                Text(c, lobbyNames[i], r.X + 110, r.Y + 13, 19, White, true);
            }
            Button(c, BackBtn, "LEAVE", true);
        }

        void DrawCenterMsg(DrawingContext c, string title, string sub)
        {
            Text(c, title, TableCx, 200, 44, Gold, true, true);
            if (string.IsNullOrEmpty(sub)) return;
            double y = 280;
            foreach (var line in sub.Split('\n')) { Text(c, line, TableCx, y, 18, White, false, true); y += 30; }
        }

        // ---------------------------------------------------------- game --
        Point lastPointer;

        void DrawGame(DrawingContext c)
        {
            if (!haveState) { DrawCenterMsg(c, "Dealing...", ""); return; }

            // pond
            if (deckCount > 0)
            {
                int shown = Math.Min(4, deckCount);
                for (int i = shown - 1; i >= 0; i--)
                    Back(c, new Rect(DeckRect.X - i * 2, DeckRect.Y - i * 2, CardW, CardH));
            }
            else
            {
                c.DrawRectangle(null, ThinPen, DeckRect, 10, 10);
            }
            Text(c, "POND", DeckRect.Center.X, DeckRect.Bottom + 10, 14, Faint, true, true);
            Text(c, deckCount + " left", DeckRect.Center.X, DeckRect.Bottom + 30, 14, Faint, false, true);

            // opponents
            for (int i = 0; i < names.Length; i++)
            {
                if (i == you) continue;
                DrawOpponent(c, i);
            }

            // your books + name
            var mine = boxes[you];
            Text(c, names.Length > you ? names[you] + " (you)" : "you", 200, 512, 19, Gold, true);
            Text(c, "books: " + (books.Length > you ? books[you].Length : 0), 200, 540, 15, Faint);
            DrawBooks(c, books.Length > you ? books[you] : new int[0], 330, 506, false);

            // banner
            if (!over)
            {
                string msg = banner;
                if (turn == you)
                {
                    var t = Targets();
                    msg = selRank > 0
                        ? "Asking for " + Card.WordOf(selRank) + " — now click a player"
                        : (t.Count == 1 ? "Your turn — click a card to ask" : "Your turn — click a card, then a player");
                }
                Text(c, msg, TableCx, 440, 22, turn == you ? Gold : White, true, true);
            }

            // hand
            var rects = HandRects();
            for (int i = 0; i < rects.Count; i++)
            {
                bool pick = hand[i].Rank == selRank;
                Front(c, rects[i], hand[i], pick || i == hovered);
                if (pick) c.DrawRectangle(null, HotPen, rects[i], 9, 9);
            }
            if (hand.Count == 0)
                Text(c, "your hand is empty", TableCx, 700, 18, Faint, false, true);

            DrawLog(c);
            DrawFlights(c);

            if (over)
            {
                c.DrawRectangle(Dim, null, new Rect(0, 0, W, H));
                Text(c, "GAME OVER", TableCx, 250, 54, Gold, true, true);
                Text(c, banner, TableCx, 330, 26, White, true, true);

                var order = Enumerable.Range(0, names.Length)
                                      .OrderByDescending(i => books[i].Length).ToList();
                double y = 380;
                foreach (int i in order)
                {
                    Text(c, names[i] + (i == you ? " (you)" : "") + "  —  " + books[i].Length + " books",
                         TableCx, y, 19, i == you ? Gold : White, i == you, true);
                    y += 28;
                }

                if (host != null) { Button(c, new Rect(TableCx - 100, y + 16, 200, 48), "PLAY AGAIN", true); }
                else Text(c, "Waiting for the host to deal again...", TableCx, y + 24, 17, Faint, false, true);
            }
        }

        void DrawOpponent(DrawingContext c, int i)
        {
            var b = boxes[i];
            if (b == null) return;

            bool active = turn == i;
            bool target = selRank > 0 && counts[i] > 0 && turn == you;
            bool hot = hoveredSeat == i && target;

            c.DrawRectangle(Panel, active ? GoldPen : (hot ? HotPen : Edge), b.Panel, 10, 10);

            string nm = i < names.Length ? names[i] : "?";
            if (i < isCpu.Length && isCpu[i]) { }
            Text(c, nm, b.Panel.X + 12, b.Panel.Y + 8, 18, active ? Gold : White, true);
            Text(c, counts[i] + " cards   •   " + books[i].Length + " books",
                 b.Panel.X + 12, b.Panel.Y + 32, 14, Faint);

            // fanned backs
            int n = Math.Min(counts[i], 9);
            double step = n > 1 ? Math.Min(MiniW * 0.7, (b.Panel.Width - 30 - MiniW) / (n - 1)) : 0;
            double x0 = b.Panel.X + 14;
            for (int k = 0; k < n; k++)
                Back(c, new Rect(x0 + k * step, b.Panel.Y + 54, MiniW, MiniH));
            if (counts[i] > 9)
                Text(c, "+" + (counts[i] - 9), x0 + n * step + 6, b.Panel.Y + 66, 14, Faint);

            DrawBooks(c, books[i], b.Panel.X + 12, b.Panel.Bottom - 30, true);

            if (target)
                Text(c, hot ? "ASK  >" : "click to ask", b.Panel.Right - 96, b.Panel.Y + 8, 14, Gold, hot);
        }

        void DrawBooks(DrawingContext c, int[] list, double x, double y, bool mini)
        {
            double w = mini ? 20 : BookW, h = mini ? 26 : BookH, gap = mini ? 3 : 6;
            for (int i = 0; i < list.Length; i++)
            {
                var r = new Rect(x + i * (w + gap), y, w, h);
                c.DrawRectangle(Paper, Edge, r, 3, 3);
                Text(c, Card.LabelOf(list[i]), r.Center.X, r.Y + (mini ? 5 : 16), mini ? 13 : 22, Ink, true, true);
            }
        }

        void DrawLog(DrawingContext c)
        {
            var r = new Rect(LogX, 14, LogW, H - 28);
            c.DrawRectangle(Panel, Edge, r, 10, 10);
            Text(c, "TABLE LOG", r.X + 14, r.Y + 12, 15, Gold, true);

            using (c.PushClip(new Rect(r.X + 8, r.Y + 38, r.Width - 16, r.Height - 48)))
            {
                double y = r.Bottom - 22;
                for (int i = logLines.Length - 1; i >= 0 && y > r.Y + 38; i--)
                {
                    var lines = Wrap(logLines[i], 34);
                    for (int k = lines.Count - 1; k >= 0; k--)
                    {
                        Text(c, lines[k], r.X + 14, y, 13,
                             logLines[i].StartsWith("  ") ? Faint : White);
                        y -= 17;
                    }
                    y -= 4;
                }
            }
        }

        void DrawFlights(DrawingContext c)
        {
            if (flying == null) return;
            double t = flyT < 0 ? 0 : flyT > 1 ? 1 : flyT;
            double e = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;   // ease in-out

            foreach (var f in flying)
            {
                double x = f.From.X + (f.To.X - f.From.X) * e + f.Stagger;
                double y = f.From.Y + (f.To.Y - f.From.Y) * e - Math.Sin(e * Math.PI) * 48;
                var r = new Rect(x - CardW / 2, y - CardH / 2, CardW, CardH);
                if (f.FaceUp) Front(c, r, f.Card, false); else Back(c, r);
            }
        }

        // ------------------------------------------------- card painting --
        static void Front(DrawingContext c, Rect r, Card card, bool glow)
        {
            c.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null,
                            new Rect(r.X + 3, r.Y + 4, r.Width, r.Height), 9, 9);
            c.DrawRectangle(Paper, glow ? HotPen : Edge, r, 9, 9);

            var col = card.IsRed ? Red : Ink;
            double s = r.Width / CardW;

            Text(c, card.Label, r.X + 8 * s, r.Y + 5 * s, 22 * s, col, true);
            Text(c, card.Glyph, r.X + 9 * s, r.Y + 30 * s, 17 * s, col);
            Text(c, card.Glyph, r.Center.X, r.Center.Y - 22 * s, 44 * s, col, false, true);

            using (c.PushTransform(Matrix.CreateTranslation(-r.Right + 8 * s, -r.Bottom + 6 * s)
                                 * Matrix.CreateRotation(Math.PI)
                                 * Matrix.CreateTranslation(r.Right - 8 * s, r.Bottom - 6 * s)))
            {
                Text(c, card.Label, r.Right - 26 * s, r.Bottom - 34 * s, 22 * s, col, true);
            }
        }

        static void Back(DrawingContext c, Rect r)
        {
            c.DrawRectangle(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), null,
                            new Rect(r.X + 2, r.Y + 3, r.Width, r.Height), 8, 8);
            c.DrawRectangle(BackA, Edge, r, 8, 8);
            var inner = r.Deflate(5);
            c.DrawRectangle(BackB, null, inner, 5, 5);

            using (c.PushClip(inner))
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1);
                for (double d = -inner.Height; d < inner.Width; d += 8)
                {
                    c.DrawLine(pen, new Point(inner.X + d, inner.Y),
                                    new Point(inner.X + d + inner.Height, inner.Bottom));
                }
            }
            c.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1), inner, 5, 5);
        }

        // ---------------------------------------------------- ui helpers --
        void Button(DrawingContext c, Rect r, string label, bool enabled)
        {
            bool hot = enabled && r.Contains(lastPointer);
            var bg = enabled ? (hot ? new SolidColorBrush(Color.FromRgb(38, 110, 78)) : Panel2) : Panel;
            c.DrawRectangle(bg, enabled ? (hot ? HotPen : GoldPen) : Edge, r, 8, 8);
            Text(c, label, r.Center.X, r.Center.Y - 12, 20, enabled ? White : Faint, true, true);
        }

        static void Text(DrawingContext c, string s, double x, double y, double size,
                         IBrush brush, bool bold = false, bool center = false)
        {
            if (string.IsNullOrEmpty(s)) return;
            var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                       bold ? Bold : Face, size, brush);
            c.DrawText(ft, new Point(center ? x - ft.Width / 2 : x, y));
        }

        static List<string> Wrap(string s, int width)
        {
            var out_ = new List<string>();
            if (string.IsNullOrEmpty(s)) { out_.Add(""); return out_; }
            var words = s.Split(' ');
            var cur = "";
            foreach (var w in words)
            {
                if (cur.Length > 0 && cur.Length + w.Length + 1 > width) { out_.Add(cur); cur = w; }
                else cur = cur.Length == 0 ? w : cur + " " + w;
            }
            if (cur.Length > 0) out_.Add(cur);
            return out_;
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            hovered = -1; hoveredSeat = -1; InvalidateVisual();
        }

        // keep hover state for buttons drawn in immediate mode
        protected override void OnPointerEntered(PointerEventArgs e) => lastPointer = e.GetPosition(this);

        public void TrackPointer(Point p) { lastPointer = p; }
    }

    // =====================================================================
    //  SHELL
    // =====================================================================

    public sealed class MainWindow : Window
    {
        public MainWindow()
        {
            Title = "Go Fish — 2 to 4 Players";
            Width = 1240; Height = 800;
            CanResize = false;
            var view = new GameView();
            Content = view;

            // The renderer draws buttons in immediate mode, so it needs the
            // live cursor position for hover states.
            PointerMoved += (_, e) => { view.TrackPointer(e.GetPosition(view)); view.InvalidateVisual(); };
            Opened += (_, __) => view.Focus();
        }
    }

    public sealed class App : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = new MainWindow();
            base.OnFrameworkInitializationCompleted();
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
            => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
    }
}
