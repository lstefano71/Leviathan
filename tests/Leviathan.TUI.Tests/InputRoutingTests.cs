using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using Hex1b.Widgets;

namespace Leviathan.TUI.Tests;

/// <summary>
/// Integration tests using Hex1b's virtual terminal to verify input routing.
/// Tests key bindings (Escape, Ctrl+O, Ctrl+P), AnyCharacter filtering,
/// and modal state toggling through the framework's full input pipeline.
/// </summary>
public class InputRoutingTests : IAsyncLifetime
{
    private Hex1bTerminal? _terminal;
    private CancellationTokenSource? _cts;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
            _cts = null;
        }
        if (_terminal is not null)
        {
            await _terminal.DisposeAsync();
            _terminal = null;
        }
    }

    /// <summary>
    /// Builds a headless virtual terminal running a Hex1bApp with the given widget builder.
    /// Uses the same CreateBuilder().WithHex1bApp() pattern as the production app.
    /// </summary>
    private Hex1bTerminal BuildTerminal(Func<Hex1bApp, Hex1bAppOptions, Func<RootContext, Hex1bWidget>> configure)
    {
        _terminal = Hex1bTerminal.CreateBuilder()
            .WithDimensions(80, 24)
            .WithHeadless()
            .WithHex1bApp(configure)
            .Build();
        _cts = new CancellationTokenSource();
        _ = _terminal.RunAsync(_cts.Token);
        return _terminal;
    }

    /// <summary>Waits for the terminal to render content containing the given text.</summary>
    private static Hex1bTerminalInputSequence WaitForText(string text)
    {
        return new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.GetScreenText().Contains(text), TimeSpan.FromSeconds(5), $"Wait for '{text}'")
            .Build();
    }

    // ─── Escape Key Tests ───

    [Fact]
    public async Task Escape_OnPlainInteractable_FiresBinding()
    {
        bool escapeFired = false;

        Hex1bTerminal terminal = BuildTerminal((app, options) => ctx =>
            ctx.Interactable(ic => ic.Text("Press Escape"))
                .WithInputBindings(b =>
                {
                    b.Key(Hex1bKey.Escape).Action(() => { escapeFired = true; }, "Close");
                }));

        Hex1bTerminalInputSequence seq = new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.GetScreenText().Contains("Press Escape"), TimeSpan.FromSeconds(5), "App rendered")
            .Escape()
            .Wait(200)
            .Build();
        await seq.ApplyAsync(terminal, CancellationToken.None);

        Assert.True(escapeFired, "Escape key binding should fire on a plain Interactable");
    }

    [Fact]
    public async Task Escape_WithAnyCharacterBinding_FiresEscapeNotAnyChar()
    {
        bool escapeFired = false;
        string? receivedText = null;

        Hex1bTerminal terminal = BuildTerminal((app, options) => ctx =>
            ctx.Interactable(ic => ic.Text("Press Escape"))
                .WithInputBindings(b =>
                {
                    b.Key(Hex1bKey.Escape).Action(() => { escapeFired = true; }, "Close");
                    b.AnyCharacter().Action(text => { receivedText = text; }, "Type");
                }));

        Hex1bTerminalInputSequence seq = new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.GetScreenText().Contains("Press Escape"), TimeSpan.FromSeconds(5), "App rendered")
            .Escape()
            .Wait(200)
            .Build();
        await seq.ApplyAsync(terminal, CancellationToken.None);

        Assert.True(escapeFired, "Escape binding should fire even when AnyCharacter is also bound");
        Assert.Null(receivedText);
    }

    [Fact]
    public async Task Escape_WrappedInPastableWidget_FiresBinding()
    {
        bool escapeFired = false;

        Hex1bTerminal terminal = BuildTerminal((app, options) => ctx =>
        {
            Hex1bWidget inner = ctx.Interactable(ic => ic.Text("Press Escape"))
                .WithInputBindings(b =>
                {
                    b.Key(Hex1bKey.Escape).Action(() => { escapeFired = true; }, "Close");
                    b.AnyCharacter().Action(_ => { }, "Type");
                });
            return ctx.Pastable(inner);
        });

        Hex1bTerminalInputSequence seq = new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.GetScreenText().Contains("Press Escape"), TimeSpan.FromSeconds(5), "App rendered")
            .Escape()
            .Wait(200)
            .Build();
        await seq.ApplyAsync(terminal, CancellationToken.None);

        Assert.True(escapeFired, "Escape should fire even when wrapped in PastableWidget");
    }

    // ─── Ctrl+O Tests ───

    [Fact]
    public async Task CtrlO_OnPlainInteractable_FiresBinding()
    {
        bool ctrlOFired = false;

        Hex1bTerminal terminal = BuildTerminal((app, options) => ctx =>
            ctx.Interactable(ic => ic.Text("Press Ctrl+O"))
                .WithInputBindings(b =>
                {
                    b.Ctrl().Key(Hex1bKey.O).Action(() => { ctrlOFired = true; }, "Open");
                }));

        Hex1bTerminalInputSequence seq = new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.GetScreenText().Contains("Press Ctrl+O"), TimeSpan.FromSeconds(5), "App rendered")
            .Ctrl().Key(Hex1bKey.O)
            .Wait(200)
            .Build();
        await seq.ApplyAsync(terminal, CancellationToken.None);

        Assert.True(ctrlOFired, "Ctrl+O binding should fire");
    }

    [Fact]
    public async Task CtrlO_WithAnyCharacterBinding_FiresCtrlONotAnyChar()
    {
        bool ctrlOFired = false;
        string? receivedText = null;

        Hex1bTerminal terminal = BuildTerminal((app, options) => ctx =>
            ctx.Interactable(ic => ic.Text("Press Ctrl+O"))
                .WithInputBindings(b =>
                {
                    b.Ctrl().Key(Hex1bKey.O).Action(() => { ctrlOFired = true; }, "Open");
                    b.AnyCharacter().Action(text => { receivedText = text; }, "Type");
                }));

        Hex1bTerminalInputSequence seq = new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.GetScreenText().Contains("Press Ctrl+O"), TimeSpan.FromSeconds(5), "App rendered")
            .Ctrl().Key(Hex1bKey.O)
            .Wait(200)
            .Build();
        await seq.ApplyAsync(terminal, CancellationToken.None);

        Assert.True(ctrlOFired, "Ctrl+O binding should fire even when AnyCharacter is also bound");
        Assert.Null(receivedText);
    }

    // ─── Ctrl+P baseline ───

    [Fact]
    public async Task CtrlP_WithAnyCharacterBinding_FiresCtrlP()
    {
        bool ctrlPFired = false;

        Hex1bTerminal terminal = BuildTerminal((app, options) => ctx =>
            ctx.Interactable(ic => ic.Text("Press Ctrl+P"))
                .WithInputBindings(b =>
                {
                    b.Ctrl().Key(Hex1bKey.P).Action(() => { ctrlPFired = true; }, "Palette");
                    b.AnyCharacter().Action(_ => { }, "Type");
                }));

        Hex1bTerminalInputSequence seq = new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.GetScreenText().Contains("Press Ctrl+P"), TimeSpan.FromSeconds(5), "App rendered")
            .Ctrl().Key(Hex1bKey.P)
            .Wait(200)
            .Build();
        await seq.ApplyAsync(terminal, CancellationToken.None);

        Assert.True(ctrlPFired, "Ctrl+P should fire (baseline — known working key)");
    }

    // ─── Escape + arrow fragment test ───

    [Fact]
    public async Task EscapeThenArrow_DoesNotLeakFragmentsToAnyCharacter()
    {
        string receivedText = "";

        Hex1bTerminal terminal = BuildTerminal((app, options) => ctx =>
            ctx.Interactable(ic => ic.Text("Type something"))
                .WithInputBindings(b =>
                {
                    b.Key(Hex1bKey.Escape).Action(() => { }, "Close");
                    b.Key(Hex1bKey.UpArrow).Action(() => { }, "Up");
                    b.AnyCharacter().Action(text => { receivedText += text; }, "Type");
                }));

        Hex1bTerminalInputSequence seq = new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.GetScreenText().Contains("Type something"), TimeSpan.FromSeconds(5), "App rendered")
            .Escape()
            .Up()
            .Wait(200)
            .Build();
        await seq.ApplyAsync(terminal, CancellationToken.None);

        Assert.Equal("", receivedText);
    }

    // ─── Binding order: Key().Ctrl() vs Ctrl().Key() ───

    [Fact]
    public async Task CtrlO_KeyThenCtrl_FiresBinding()
    {
        bool ctrlOFired = false;

        Hex1bTerminal terminal = BuildTerminal((app, options) => ctx =>
            ctx.Interactable(ic => ic.Text("Press Ctrl+O"))
                .WithInputBindings(b =>
                {
                    b.Key(Hex1bKey.O).Ctrl().Action(() => { ctrlOFired = true; }, "Open");
                    b.AnyCharacter().Action(_ => { }, "Type");
                }));

        Hex1bTerminalInputSequence seq = new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.GetScreenText().Contains("Press Ctrl+O"), TimeSpan.FromSeconds(5), "App rendered")
            .Ctrl().Key(Hex1bKey.O)
            .Wait(200)
            .Build();
        await seq.ApplyAsync(terminal, CancellationToken.None);

        Assert.True(ctrlOFired, "Ctrl+O with Key().Ctrl() order should also fire");
    }

    // ─── Modal state toggling (simulates real app workflow) ───

    [Fact]
    public async Task CtrlF_ThenEscape_TogglesModalState()
    {
        bool findBarOpen = false;

        Hex1bTerminal terminal = BuildTerminal((app, options) => ctx =>
        {
            string label = findBarOpen ? "FindBar Open" : "Normal";
            return ctx.Interactable(ic => ic.Text(label))
                .WithInputBindings(b =>
                {
                    b.Ctrl().Key(Hex1bKey.F).Action(() => { findBarOpen = !findBarOpen; }, "Find");
                    b.Key(Hex1bKey.Escape).Action(() => { findBarOpen = false; }, "Close");
                    b.AnyCharacter().Action(_ => { }, "Type");
                });
        });

        // Wait for initial render
        await WaitForText("Normal").ApplyAsync(terminal, CancellationToken.None);

        // Open find bar with Ctrl+F
        Hex1bTerminalInputSequence openSeq = new Hex1bTerminalInputSequenceBuilder()
            .Ctrl().Key(Hex1bKey.F)
            .WaitUntil(s => s.GetScreenText().Contains("FindBar Open"), TimeSpan.FromSeconds(3), "Find bar opened")
            .Build();
        await openSeq.ApplyAsync(terminal, CancellationToken.None);
        Assert.True(findBarOpen, "Ctrl+F should open find bar");

        // Close with Escape
        Hex1bTerminalInputSequence closeSeq = new Hex1bTerminalInputSequenceBuilder()
            .Escape()
            .WaitUntil(s => s.GetScreenText().Contains("Normal"), TimeSpan.FromSeconds(3), "Find bar closed")
            .Build();
        await closeSeq.ApplyAsync(terminal, CancellationToken.None);
        Assert.False(findBarOpen, "Escape should close find bar");
    }

    [Fact]
    public async Task CtrlP_ThenEscape_TogglesModalState()
    {
        bool paletteOpen = false;

        Hex1bTerminal terminal = BuildTerminal((app, options) => ctx =>
        {
            string label = paletteOpen ? "Palette Open" : "Normal";
            return ctx.Interactable(ic => ic.Text(label))
                .WithInputBindings(b =>
                {
                    b.Ctrl().Key(Hex1bKey.P).Action(() => { paletteOpen = !paletteOpen; }, "Palette");
                    b.Key(Hex1bKey.Escape).Action(() => { paletteOpen = false; }, "Close");
                    b.AnyCharacter().Action(_ => { }, "Type");
                });
        });

        await WaitForText("Normal").ApplyAsync(terminal, CancellationToken.None);

        // Open palette with Ctrl+P
        Hex1bTerminalInputSequence openSeq = new Hex1bTerminalInputSequenceBuilder()
            .Ctrl().Key(Hex1bKey.P)
            .WaitUntil(s => s.GetScreenText().Contains("Palette Open"), TimeSpan.FromSeconds(3), "Palette opened")
            .Build();
        await openSeq.ApplyAsync(terminal, CancellationToken.None);
        Assert.True(paletteOpen, "Ctrl+P should open command palette");

        // Close with Escape
        Hex1bTerminalInputSequence closeSeq = new Hex1bTerminalInputSequenceBuilder()
            .Escape()
            .WaitUntil(s => s.GetScreenText().Contains("Normal"), TimeSpan.FromSeconds(3), "Palette closed")
            .Build();
        await closeSeq.ApplyAsync(terminal, CancellationToken.None);
        Assert.False(paletteOpen, "Escape should close command palette");
    }

    [Fact]
    public async Task CtrlO_TriggersFileOpenState()
    {
        bool fileOpenTriggered = false;

        Hex1bTerminal terminal = BuildTerminal((app, options) => ctx =>
        {
            string label = fileOpenTriggered ? "File Open" : "Normal";
            return ctx.Interactable(ic => ic.Text(label))
                .WithInputBindings(b =>
                {
                    b.Ctrl().Key(Hex1bKey.O).Action(() => { fileOpenTriggered = true; }, "Open");
                    b.AnyCharacter().Action(_ => { }, "Type");
                });
        });

        await WaitForText("Normal").ApplyAsync(terminal, CancellationToken.None);

        Hex1bTerminalInputSequence seq = new Hex1bTerminalInputSequenceBuilder()
            .Ctrl().Key(Hex1bKey.O)
            .WaitUntil(s => s.GetScreenText().Contains("File Open"), TimeSpan.FromSeconds(3), "File open triggered")
            .Build();
        await seq.ApplyAsync(terminal, CancellationToken.None);

        Assert.True(fileOpenTriggered, "Ctrl+O should trigger file open");
    }

    // ─── IsSafeText filter tests ───

    [Fact]
    public async Task AnyCharacter_NormalText_Received()
    {
        string receivedText = "";

        Hex1bTerminal terminal = BuildTerminal((app, options) => ctx =>
            ctx.Interactable(ic => ic.Text("Ready"))
                .WithInputBindings(b =>
                {
                    b.AnyCharacter().Action(text => { receivedText += text; }, "Type");
                }));

        await WaitForText("Ready").ApplyAsync(terminal, CancellationToken.None);

        Hex1bTerminalInputSequence seq = new Hex1bTerminalInputSequenceBuilder()
            .Type("hello")
            .Wait(200)
            .Build();
        await seq.ApplyAsync(terminal, CancellationToken.None);

        Assert.Equal("hello", receivedText);
    }

    [Fact]
    public async Task MultipleCtrlKeys_AllFire()
    {
        HashSet<string> firedKeys = new();

        Hex1bTerminal terminal = BuildTerminal((app, options) => ctx =>
            ctx.Interactable(ic => ic.Text("Ready"))
                .WithInputBindings(b =>
                {
                    b.Ctrl().Key(Hex1bKey.Q).Action(() => firedKeys.Add("Q"), "Quit");
                    b.Ctrl().Key(Hex1bKey.S).Action(() => firedKeys.Add("S"), "Save");
                    b.Ctrl().Key(Hex1bKey.F).Action(() => firedKeys.Add("F"), "Find");
                    b.Ctrl().Key(Hex1bKey.G).Action(() => firedKeys.Add("G"), "Goto");
                    b.AnyCharacter().Action(_ => { }, "Type");
                }));

        await WaitForText("Ready").ApplyAsync(terminal, CancellationToken.None);

        Hex1bTerminalInputSequence seq = new Hex1bTerminalInputSequenceBuilder()
            .Ctrl().Key(Hex1bKey.S)
            .Wait(100)
            .Ctrl().Key(Hex1bKey.F)
            .Wait(100)
            .Ctrl().Key(Hex1bKey.G)
            .Wait(200)
            .Build();
        await seq.ApplyAsync(terminal, CancellationToken.None);

        Assert.Contains("S", firedKeys);
        Assert.Contains("F", firedKeys);
        Assert.Contains("G", firedKeys);
    }
}
