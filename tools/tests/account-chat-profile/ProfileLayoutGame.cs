using System.Reflection;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.IO.Stores;
using osu.Framework.Testing;
using osu.Game.Graphics.Containers.Markdown.BBCode;
using osu.Game.Overlays;
using osu.Game.Configuration;
using osu.Game.Graphics;
using osu.Game.Resources;

// Exercises real drawable loading and layout, rather than only accepting BBCode tokens.
public partial class ProfileLayoutGame : Game
{
    [Cached]
    private readonly OverlayColourProvider colours = new(OverlayColourScheme.Pink);
    [Cached]
    private readonly SessionStatics statics = new();
    [Cached]
    private readonly OsuColour osuColour = new();
    private readonly string text;
    private ProfileMarkdownContainer profile = null!;
    private int frames;
    private float collapsedHeight;
    private Exception? failure;
    private bool completed;

    public ProfileLayoutGame(string text) => this.text = text;

    [BackgroundDependencyLoader]
    private void load()
    {
        Resources.AddStore(new DllResourceStore(OsuResources.ResourceAssembly));
        AddFont(Resources, "Fonts/Inter/Inter-Regular");
        AddFont(Resources, "Fonts/Inter/Inter-Bold");
        AddFont(Resources, "Fonts/Torus/Torus-Regular");
        Add(profile = new ProfileMarkdownContainer
        {
            Width = 800,
            AutoSizeAxes = Axes.Y,
            Text = BbCodeInlineParser.Prepare(text),
        });
    }

    protected override void UpdateAfterChildren()
    {
        base.UpdateAfterChildren();
        if (++frames < 20) return;
        try
        {
            var blocks = descendants(profile).OfType<ProfileBbCodeBlock>().ToArray();
            var box = blocks.Single(b => b.Header != null);
            if (frames == 20)
            {
                if (blocks.All(b => !b.IsCentred)) throw new Exception("Centred drawable was not rendered");
                if (box.DrawWidth < 600) throw new Exception("BBCode box does not fill the profile width");
                collapsedHeight = profile.DrawHeight;
                box.Header!.Action();
            }
            if (frames < 40) return;
            if (profile.DrawHeight <= collapsedHeight) throw new Exception("Expanded box does not participate in page layout");
            var labels = descendants(profile).OfType<SpriteText>().Select(t => t.Text.ToString()).ToArray();
            if (!labels.Any(t => t.Contains("ACHIEVED"))) throw new Exception("Box body was not rendered");
            if (labels.Any(t => t.Contains("[centre]") || t.Contains("[/centre]") || t.Contains("[box=") || t.Contains("[/box]") || t.Contains('\u200b')))
                throw new Exception("Raw BBCode or paragraph marker leaked into rendered labels");
            completed = true;
            Exit();
        }
        catch (Exception e) { failure = e; Exit(); }
    }

    private static IEnumerable<Drawable> descendants(Drawable root)
    {
        yield return root;
        if (root is not CompositeDrawable composite) yield break;
        var children = (IReadOnlyList<Drawable>)typeof(CompositeDrawable).GetProperty("InternalChildren", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(composite)!;
        foreach (var child in children)
            foreach (var descendant in descendants(child)) yield return descendant;
    }

    public static void Verify(string text)
    {
        using var host = new TestRunHeadlessGameHost("profile-bbcode-" + Guid.NewGuid().ToString("N"));
        using var game = new ProfileLayoutGame(text);
        var context = SynchronizationContext.Current;
        try { host.Run(game); }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
        if (game.failure != null) throw new Exception("Profile layout regression", game.failure);
        if (!game.completed) throw new Exception("Profile layout test did not finish");
        Console.WriteLine("PASS profile drawable layout: full-width blocks, expanding boxes and no literal tags/markers");
    }
}
