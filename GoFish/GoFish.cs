// ============================================================================
//  GO FISH  —  C# / .NET + Avalonia 11   (Windows • macOS • Linux)
//
//  Single file. Cards are drawn by hand with Avalonia's DrawingContext,
//  so there are zero image assets. Animated card transfers, fanned hand
//  with hover-lift, and an AI opponent that remembers what you ask for.
//
//  BUILD:
//    mkdir GoFish && cd GoFish
//    dotnet new console -o . -n GoFish
//    # delete Program.cs, drop in GoFish.cs, then replace GoFish.csproj with:
//
//    <Project Sdk="Microsoft.NET.Sdk">
//      <PropertyGroup>
//        <OutputType>WinExe</OutputType>
//        <TargetFramework>net8.0</TargetFramework>
//        <Nullable>disable</Nullable>
//        <BuiltInComInteropSupport>true</BuiltInComInteropSupport>
//        <ApplicationManifest>app.manifest</ApplicationManifest>
//      </PropertyGroup>
//      <ItemGroup>
//        <PackageReference Include="Avalonia" Version="11.0.10" />
//        <PackageReference Include="Avalonia.Desktop" Version="11.0.10" />
//        <PackageReference Include="Avalonia.Themes.Fluent" Version="11.0.10" />
//      </ItemGroup>
//    </Project>
//
//    (drop the <ApplicationManifest> line if you don't have an app.manifest)
//
//    dotnet run
//
//  CONTROLS: click a card in your hand to ask the opponent for that rank.
//            R = restart once the game is over.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Themes.Fluent;

namespace GoFish
{
    // ---------------------------------------------------------------- model --
    public enum Suit { Clubs, Diamonds, Hearts, Spades }

    public sealed class Card
    {
        public int Rank { get; }              // 1 = Ace ... 13 = King
        public Suit Suit { get; }
        public Card(int rank, Suit suit) { Rank = rank; Suit = suit; }

        static readonly string[] Labels =
            { "", "A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K" };
        static readonly string[] Words =
            { "", "Ace", "Two", "Three", "Four", "Five", "Six", "Seven",
              "Eight", "Nine", "Ten", "Jack", "Queen", "King" };

        public string Label => Labels[Rank];
        public static string LabelOf(int rank) => Labels[rank];
        public static string WordOf(int rank) => Words[rank];

        public string Glyph => Suit switch
        {
            Suit.Clubs    => "\u2663",
            Suit.Diamonds => "\u2666",
            Suit.Hearts   => "\u2665",
            _             => "\u2660"
        };

        public bool IsRed => Suit == Suit.Diamonds || Suit == Suit.Hearts;
    }

    sealed class FlyingCard
    {
        public Card Card;
        public Point From, To;
        public bool FaceUp;
        public double Stagger;   // so a stack of three doesn't overlap perfectly
    }

    // ------------------------------------------------------------------ view --
    public sealed class GameView : Control
    {
        // ---- geometry ----
        const double CardW = 92, CardH = 132;
        const double BookW = 46, BookH = 66;
        const double TableCx = 420;
        const double OppHandY = 26;
        const double PlayerHandY = 578;
        const double LogX = 848, LogW = 252;

        static readonly Rect DeckRect  = new Rect(112, 300, CardW, CardH);
        static readonly Rect OppBooks  = new Rect(292, 190, 520, BookH);
        static readonly Rect SelfBooks = new Rect(292, 424, 520, BookH);
        static readonly Rect RestartBtn = new Rect(TableCx - 95, 388, 190, 48);

        static Point DeckCenter   => DeckRect.Center;
        static Point PlayerCenter => new Point(TableCx, PlayerHandY + CardH / 2);
        static Point OppCenter    => new Point(TableCx, OppHandY + CardH / 2);

        // ---- palette ----
        static readonly IBrush Felt     = new SolidColorBrush(Color.FromRgb(18, 62, 44));
        static readonly IBrush FeltLite = new SolidColorBrush(Color.FromRgb(28, 88, 62));
        static readonly IBrush Ink      = new SolidColorBrush(Color.FromRgb(24, 26, 32));
        static readonly IBrush Red      = new SolidColorBrush(Color.FromRgb(198, 40, 44));
        static readonly IBrush Paper    = new SolidColorBrush(Color.FromRgb(250, 249, 245));
        static readonly IBrush BackA    = new SolidColorBrush(Color.FromRgb(28, 58, 122));
        static readonly IBrush BackB    = new SolidColorBrush(Color.FromRgb(52, 96, 178));
        static readonly IBrush Panel    = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
        static readonly IBrush Gold     = new SolidColorBrush(Color.FromRgb(240, 196, 88));
        static readonly IBrush Dim      = new SolidColorBrush(Color.FromArgb(190, 8, 26, 20));
        static readonly IPen   Edge     = new Pen(new SolidColorBrush(Color.FromRgb(38, 40, 46)), 1.4);
        static readonly IPen   GoldPen  = new Pen(Gold, 2);
        static readonly IPen   HotPen   = new Pen(new SolidColorBrush(Color.FromRgb(255, 226, 130)), 3);

        // ---- state ----
        readonly Random rng = new Random();
        List<Card> deck = new List<Card>();
        List<Card> playerHand = new List<Card>();
        List<Card> aiHand = new List<Card>();
        readonly List<int> playerBooks = new List<int>();
        readonly List<int> aiBooks = new List<int>();
        readonly HashSet<int> aiMemory = new HashSet<int>();  // ranks AI thinks you hold
        readonly List<string> log = new List<string>();

        bool playerTurn = true;
        bool inputLocked = true;
        int hovered = -1;

        List<FlyingCard> flying;
        double flyT;

        public GameView()
        {
            Width = 1100; Height = 740;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Arrow);
            NewGame();
        }

        // ------------------------------------------------------------- setup --
        void NewGame()
        {
            deck.Clear(); playerHand.Clear(); aiHand.Clear();
            playerBooks.Clear(); aiBooks.Clear(); aiMemory.Clear(); log.Clear();
            flying = null; hovered = -1;

            for (int r = 1; r <= 13; r++)
                foreach (Suit s in Enum.GetValues(typeof(Suit)))
                    deck.Add(new Card(r, s));

            for (int i = deck.Count - 1; i > 0; i--)          // Fisher–Yates
            {
                int j = rng.Next(i + 1);
                (deck[i], deck[j]) = (deck[j], deck[i]);
            }

            for (int i = 0; i < 7; i++) { playerHand.Add(Draw()); aiHand.Add(Draw()); }
            SortHand();

            Log("Dealt seven each. Click a card to ask for that rank.");
            CheckBooks(true);
            CheckBooks(false);

            playerTurn = true;
            inputLocked = false;
            InvalidateVisual();
        }

        Card Draw()
        {
            var c = deck[^1];
            deck.RemoveAt(deck.Count - 1);
            return c;
        }

        void SortHand() =>
            playerHand = playerHand.OrderBy(c => c.Rank).ThenBy(c => (int)c.Suit).ToList();

        void Log(string msg)
        {
            log.Add(msg);
            if (log.Count > 60) log.RemoveAt(0);
            InvalidateVisual();
        }

        bool IsGameOver =>
            playerBooks.Count + aiBooks.Count == 13 ||
            (deck.Count == 0 && playerHand.Count == 0 && aiHand.Count == 0);

        // ------------------------------------------------------------- input --
        protected override void OnPointerMoved(PointerEventArgs e)
        {
            int h = HitTestHand(e.GetPosition(this));
            if (h != hovered) { hovered = h; InvalidateVisual(); }
        }

        protected override async void OnPointerPressed(PointerPressedEventArgs e)
        {
            var p = e.GetPosition(this);

            if (IsGameOver)
            {
                if (RestartBtn.Contains(p)) NewGame();
                return;
            }
            if (inputLocked || !playerTurn) return;

            int i = HitTestHand(p);
            if (i < 0) return;

            inputLocked = true;
            hovered = -1;
            await PlayerAsk(playerHand[i].Rank);
        }

        public void OnKey(Key k)
        {
            if (k == Key.R && IsGameOver) NewGame();
        }

        int HitTestHand(Point p)
        {
            var rects = HandRects();
            for (int i = rects.Count - 1; i >= 0; i--)      // topmost card first
                if (rects[i].Contains(p)) return i;
            return -1;
        }

        List<Rect> HandRects()
        {
            var list = new List<Rect>();
            int n = playerHand.Count;
            if (n == 0) return list;

            const double maxSpan = 720;
            double step = (n * (CardW + 6) <= maxSpan) ? CardW + 6 : (maxSpan - CardW) / (n - 1);
            double total = (n - 1) * step + CardW;
            double x0 = TableCx - total / 2;

            for (int i = 0; i < n; i++)
            {
                double lift = (i == hovered && playerTurn && !inputLocked) ? 24 : 0;
                list.Add(new Rect(x0 + i * step, PlayerHandY - lift, CardW, CardH));
            }
            return list;
        }

        List<Rect> OppRects()
        {
            var list = new List<Rect>();
            int n = aiHand.Count;
            if (n == 0) return list;

            const double maxSpan = 720;
            double step = (n * (CardW + 6) <= maxSpan) ? CardW + 6 : (maxSpan - CardW) / (n - 1);
            double total = (n - 1) * step + CardW;
            double x0 = TableCx - total / 2;

            for (int i = 0; i < n; i++)
                list.Add(new Rect(x0 + i * step, OppHandY, CardW, CardH));
            return list;
        }

        // --------------------------------------------------------- animation --
        async Task Fly(List<Card> cards, Point from, Point to, bool faceUp)
        {
            flying = cards.Select((c, i) => new FlyingCard
            {
                Card = c, From = from, To = to, FaceUp = faceUp,
                Stagger = (i - (cards.Count - 1) / 2.0) * 26
            }).ToList();

            const int frames = 26;
            for (int f = 0; f <= frames; f++)
            {
                flyT = f / (double)frames;
                InvalidateVisual();
                await Task.Delay(14);
            }
            flying = null;
            InvalidateVisual();
        }

        static double Ease(double t) =>
            t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;

        // ---------------------------------------------------------- game flow --
        void CheckBooks(bool player)
        {
            var hand = player ? playerHand : aiHand;
            var books = player ? playerBooks : aiBooks;

            foreach (int rank in hand.GroupBy(c => c.Rank)
                                     .Where(g => g.Count() == 4)
                                     .Select(g => g.Key).ToList())
            {
                hand.RemoveAll(c => c.Rank == rank);
                books.Add(rank);
                books.Sort();
                aiMemory.Remove(rank);
                Log($"{(player ? "You book" : "Opponent books")} the {Card.WordOf(rank)}s! " +
                    $"({books.Count} set{(books.Count == 1 ? "" : "s")})");
            }
        }

        async Task RefillIfEmpty(bool player)
        {
            var hand = player ? playerHand : aiHand;
            if (hand.Count > 0 || deck.Count == 0) return;

            int n = Math.Min(7, deck.Count);
            var drawn = new List<Card>();
            for (int i = 0; i < n; i++) drawn.Add(Draw());

            await Fly(drawn, DeckCenter, player ? PlayerCenter : OppCenter, player);
            hand.AddRange(drawn);
            if (player) SortHand();

            Log($"{(player ? "You restock" : "Opponent restocks")} {n} from the pond.");
            CheckBooks(player);
        }

        async Task PlayerAsk(int rank)
        {
            Log($"You: \"Got any {Card.WordOf(rank)}s?\"");
            aiMemory.Add(rank);                                   // AI notes what you want

            var matches = aiHand.Where(c => c.Rank == rank).ToList();
            if (matches.Count > 0)
            {
                aiHand.RemoveAll(c => c.Rank == rank);
                await Fly(matches, OppCenter, PlayerCenter, true);
                playerHand.AddRange(matches);
                SortHand();
                Log($"Opponent forks over {matches.Count} {Card.WordOf(rank)}"
                    + (matches.Count == 1 ? "." : "s."));

                CheckBooks(true);
                await RefillIfEmpty(false);
                if (IsGameOver) { EndGame(); return; }

                Log("Match — you go again.");
                inputLocked = false;
                InvalidateVisual();
                return;
            }

            Log("Opponent: \"Go fish.\"");
            bool lucky = false;

            if (deck.Count > 0)
            {
                var c = Draw();
                await Fly(new List<Card> { c }, DeckCenter, PlayerCenter, true);
                playerHand.Add(c);
                SortHand();
                lucky = c.Rank == rank;
                Log(lucky
                    ? $"You fish up the {Card.WordOf(rank)} you wanted — go again!"
                    : $"You draw the {c.Label}{c.Glyph} from the pond.");
                CheckBooks(true);
            }
            else Log("The pond is empty.");

            await RefillIfEmpty(true);
            if (IsGameOver) { EndGame(); return; }

            if (lucky) { inputLocked = false; InvalidateVisual(); return; }

            await Task.Delay(420);
            await AiTurn();
        }

        int ChooseAiRank()
        {
            var mine = aiHand.Select(c => c.Rank).Distinct().ToList();

            // 1. exploit memory: a rank the AI holds AND believes you hold
            var known = mine.Where(r => aiMemory.Contains(r)).ToList();
            if (known.Count > 0) return known[rng.Next(known.Count)];

            // 2. chase its own near-books (3 of a kind, then pairs)
            var groups = aiHand.GroupBy(c => c.Rank)
                               .OrderByDescending(g => g.Count())
                               .ToList();
            if (groups[0].Count() >= 2) return groups[0].Key;

            // 3. otherwise, random
            return mine[rng.Next(mine.Count)];
        }

        async Task AiTurn()
        {
            playerTurn = false;
            InvalidateVisual();

            while (true)
            {
                await Task.Delay(620);

                await RefillIfEmpty(false);
                if (IsGameOver) { EndGame(); return; }
                if (aiHand.Count == 0) break;             // pond dry, nothing to ask with

                int rank = ChooseAiRank();
                Log($"Opponent: \"Got any {Card.WordOf(rank)}s?\"");
                await Task.Delay(340);

                var matches = playerHand.Where(c => c.Rank == rank).ToList();
                if (matches.Count > 0)
                {
                    playerHand.RemoveAll(c => c.Rank == rank);
                    await Fly(matches, PlayerCenter, OppCenter, true);
                    aiHand.AddRange(matches);
                    aiMemory.Remove(rank);
                    Log($"You hand over {matches.Count} {Card.WordOf(rank)}"
                        + (matches.Count == 1 ? "." : "s."));

                    CheckBooks(false);
                    await RefillIfEmpty(true);
                    if (IsGameOver) { EndGame(); return; }
                    continue;                              // match → opponent goes again
                }

                Log("You: \"Go fish!\"");
                if (deck.Count == 0) { Log("The pond is empty."); break; }

                var c = Draw();
                await Fly(new List<Card> { c }, DeckCenter, OppCenter, false);
                aiHand.Add(c);
                bool lucky = c.Rank == rank;
                Log(lucky ? "Opponent fishes up exactly what it wanted — it goes again."
                          : "Opponent draws from the pond.");
                CheckBooks(false);
                await RefillIfEmpty(false);
                if (IsGameOver) { EndGame(); return; }

                if (!lucky) break;
            }

            await RefillIfEmpty(true);
            if (IsGameOver) { EndGame(); return; }

            playerTurn = true;
            inputLocked = false;
            Log("Your turn.");
            InvalidateVisual();
        }

        void EndGame()
        {
            inputLocked = true;
            string verdict = playerBooks.Count > aiBooks.Count ? "You win!"
                           : playerBooks.Count < aiBooks.Count ? "Opponent wins."
                           : "Dead heat — it's a draw.";
            Log($"Game over. {playerBooks.Count}–{aiBooks.Count}. {verdict}");
            InvalidateVisual();
        }

        // ------------------------------------------------------------- render --
        static FormattedText FT(string s, double size, IBrush brush,
                                FontWeight weight = FontWeight.Bold) =>
            new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, brush);

        static void Text(DrawingContext ctx, string s, double x, double y, double size,
                         IBrush brush, bool centerX = false, bool centerY = false,
                         FontWeight w = FontWeight.Bold)
        {
            var ft = FT(s, size, brush, w);
            ctx.DrawText(ft, new Point(centerX ? x - ft.Width / 2 : x,
                                       centerY ? y - ft.Height / 2 : y));
        }

        public override void Render(DrawingContext ctx)
        {
            var full = new Rect(0, 0, Width, Height);

            // felt with a soft radial-ish wash
            ctx.FillRectangle(Felt, full);
            ctx.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                EndPoint   = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(60, 40, 120, 86), 0),
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.5),
                    new GradientStop(Color.FromArgb(70, 0, 0, 0), 1)
                }
            }, full);

            // centre divider
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), 1),
                         new Point(24, 370), new Point(824, 370));

            DrawDeck(ctx);
            DrawBookRow(ctx, OppBooks, aiBooks, "OPPONENT SETS");
            DrawBookRow(ctx, SelfBooks, playerBooks, "YOUR SETS");

            foreach (var r in OppRects()) DrawBack(ctx, r);

            var hand = HandRects();
            for (int i = 0; i < hand.Count; i++)
                DrawFace(ctx, hand[i], playerHand[i],
                         highlight: i == hovered && playerTurn && !inputLocked);

            DrawHud(ctx);
            DrawLog(ctx);

            if (flying != null)
            {
                double t = Ease(flyT);
                foreach (var f in flying)
                {
                    double x = f.From.X + (f.To.X - f.From.X) * t + f.Stagger * t;
                    double y = f.From.Y + (f.To.Y - f.From.Y) * t
                             - Math.Sin(Math.PI * t) * 54;                 // arc
                    var r = new Rect(x - CardW / 2, y - CardH / 2, CardW, CardH);
                    if (f.FaceUp) DrawFace(ctx, r, f.Card); else DrawBack(ctx, r);
                }
            }

            if (IsGameOver) DrawGameOver(ctx);
        }

        void DrawDeck(DrawingContext ctx)
        {
            int layers = Math.Min(5, Math.Max(deck.Count, 0));
            for (int i = layers - 1; i >= 0; i--)
                DrawBack(ctx, new Rect(DeckRect.X - i * 2, DeckRect.Y - i * 2, CardW, CardH));

            if (deck.Count == 0)
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 2,
                                  new DashStyle(new double[] { 4, 4 }, 0)),
                                  new RoundedRect(DeckRect, 10));

            Text(ctx, "POND", DeckRect.Center.X, DeckRect.Bottom + 12, 12,
                 new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), centerX: true);
            Text(ctx, $"{deck.Count} left", DeckRect.Center.X, DeckRect.Bottom + 30, 11,
                 new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), centerX: true,
                 w: FontWeight.Normal);
        }

        void DrawBack(DrawingContext ctx, Rect r)
        {
            var rr = new RoundedRect(r, 10);
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)),
                              new Rect(r.X + 3, r.Y + 4, r.Width, r.Height), 10);
            ctx.DrawRectangle(Paper, Edge, rr);

            var inner = new Rect(r.X + 6, r.Y + 6, r.Width - 12, r.Height - 12);
            ctx.DrawRectangle(BackA, null, new RoundedRect(inner, 7));

            using (ctx.PushClip(inner))
            {
                var pen = new Pen(BackB, 2);
                for (double d = -inner.Height; d < inner.Width; d += 9)
                    ctx.DrawLine(pen,
                        new Point(inner.X + d, inner.Y),
                        new Point(inner.X + d + inner.Height, inner.Bottom));
            }

            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1),
                              new RoundedRect(new Rect(inner.X + 5, inner.Y + 5,
                                                       inner.Width - 10, inner.Height - 10), 5));
        }

        void DrawFace(DrawingContext ctx, Rect r, Card c, bool highlight = false)
        {
            var rr = new RoundedRect(r, 10);
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
                              new Rect(r.X + 3, r.Y + 5, r.Width, r.Height), 10);
            ctx.DrawRectangle(Paper, highlight ? HotPen : Edge, rr);

            var fg = c.IsRed ? Red : Ink;

            // top-left index
            Text(ctx, c.Label, r.X + 9, r.Y + 6, 17, fg);
            Text(ctx, c.Glyph, r.X + 10, r.Y + 27, 15, fg);

            // centre pip
            Text(ctx, c.Glyph, r.Center.X, r.Center.Y + 4, 44, fg,
                 centerX: true, centerY: true, w: FontWeight.Normal);

            // bottom-right index, rotated 180°
            using (ctx.PushTransform(
                       Matrix.CreateTranslation(-r.Right + 9, -r.Bottom + 6) *
                       Matrix.CreateRotation(Math.PI) *
                       Matrix.CreateTranslation(r.Right - 9, r.Bottom - 6)))
            {
                Text(ctx, c.Label, r.Right - 9, r.Bottom - 6, 17, fg);
                Text(ctx, c.Glyph, r.Right - 10, r.Bottom - 27, 15, fg);
            }
        }

        void DrawBookRow(DrawingContext ctx, Rect area, List<int> books, string caption)
        {
            Text(ctx, caption, area.X, area.Y - 20, 11,
                 new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)));

            for (int i = 0; i < books.Count; i++)
            {
                var r = new Rect(area.X + i * (BookW + 8), area.Y, BookW, BookH);
                ctx.DrawRectangle(Paper, GoldPen, new RoundedRect(r, 6));
                Text(ctx, Card.LabelOf(books[i]), r.Center.X, r.Y + 12, 16, Ink, centerX: true);
                Text(ctx, "\u2663\u2666", r.Center.X, r.Bottom - 26, 12, Ink,
                     centerX: true, w: FontWeight.Normal);
                Text(ctx, "\u2665\u2660", r.Center.X, r.Bottom - 14, 12, Ink,
                     centerX: true, w: FontWeight.Normal);
            }
        }

        void DrawHud(DrawingContext ctx)
        {
            ctx.DrawRectangle(Panel, null, new RoundedRect(new Rect(24, 96, 200, 54), 8));
            Text(ctx, $"Opponent   {aiBooks.Count}", 40, 112, 15, Gold);

            ctx.DrawRectangle(Panel, null, new RoundedRect(new Rect(24, 496, 200, 54), 8));
            Text(ctx, $"You   {playerBooks.Count}", 40, 512, 15, Gold);

            string status = IsGameOver ? "" : playerTurn
                ? (inputLocked ? "Resolving..." : "Your turn — click a card to ask")
                : "Opponent is thinking...";
            Text(ctx, status, TableCx, 356, 13,
                 new SolidColorBrush(Color.FromArgb(215, 255, 255, 255)), centerX: true);
        }

        void DrawLog(DrawingContext ctx)
        {
            var panel = new Rect(LogX, 20, LogW, 700);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
                              new Pen(new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), 1),
                              new RoundedRect(panel, 10));
            Text(ctx, "TABLE TALK", LogX + 16, 32, 12, Gold);

            using (ctx.PushClip(new Rect(LogX + 1, 56, LogW - 2, 660)))
            {
                double y = panel.Bottom - 16;
                for (int i = log.Count - 1; i >= 0 && y > 56; i--)
                {
                    string line = log[i];
                    IBrush col = line.Contains("book", StringComparison.OrdinalIgnoreCase) ? Gold
                               : line.StartsWith("You") ? new SolidColorBrush(Color.FromRgb(168, 226, 255))
                               : line.StartsWith("Opponent") ? new SolidColorBrush(Color.FromRgb(255, 190, 170))
                               : new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));

                    var ft = FT(line, 12.5, col, FontWeight.Normal);
                    ft.MaxTextWidth = LogW - 32;
                    y -= ft.Height + 8;
                    ctx.DrawText(ft, new Point(LogX + 16, y));
                }
            }
        }

        void DrawGameOver(DrawingContext ctx)
        {
            ctx.FillRectangle(Dim, new Rect(0, 0, LogX - 10, Height));

            string verdict = playerBooks.Count > aiBooks.Count ? "YOU WIN"
                           : playerBooks.Count < aiBooks.Count ? "YOU LOSE"
                           : "DRAW";
            Text(ctx, verdict, TableCx, 268, 46, Gold, centerX: true, centerY: true);
            Text(ctx, $"{playerBooks.Count} sets  —  {aiBooks.Count} sets",
                 TableCx, 322, 18, Brushes.White, centerX: true, centerY: true);

            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(230, 34, 108, 76)), GoldPen,
                              new RoundedRect(RestartBtn, 10));
            Text(ctx, "PLAY AGAIN  (R)", RestartBtn.Center.X, RestartBtn.Center.Y, 15,
                 Brushes.White, centerX: true, centerY: true);
        }
    }

    // ------------------------------------------------------------------ shell --
    public sealed class MainWindow : Window
    {
        public MainWindow()
        {
            Title = "Go Fish";
            Width = 1100; Height = 740;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(18, 62, 44));

            var view = new GameView();
            Content = view;
            KeyDown += (_, e) => view.OnKey(e.Key);
            Opened += (_, _) => view.Focus();
        }
    }

    public sealed class App : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = new MainWindow();
            base.OnFrameworkInitializationCompleted();
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main(string[] args) =>
            AppBuilder.Configure<App>()
                      .UsePlatformDetect()
                      .LogToTrace()
                      .StartWithClassicDesktopLifetime(args);
    }
}
