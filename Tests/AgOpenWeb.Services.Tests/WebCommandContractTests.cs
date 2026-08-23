// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Static contract between the web client and the backend command wiring.
///
/// A whole class of "this button does nothing" bugs comes from a command id that the
/// browser sends but no wiring case handles (the hub's switch falls through to
/// `_ => null`, silently), or a wiring mapping to a ViewModel command that is declared
/// but never assigned (`c?.CanExecute(null)` on null → silently nothing). Today that
/// cost a field day: Zero WAS, plan-along-track patterns, the AUTO master. These tests
/// parse the sources as text so the gap is caught at build time, not in the paddock.
/// </summary>
[TestFixture]
public class WebCommandContractTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgOpenWeb.sln"))) dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "could not locate AgOpenWeb.sln above the test binary");
        return dir!.FullName;
    }

    private static string AppJs(string root) => File.ReadAllText(Path.Combine(root, "Shared", "AgOpenWeb.RemoteServer", "wwwroot", "app.js"));
    // All backend dispatch sources: the wiring project + the hub itself (control.*, diag.*).
    private static string WiringAll(string root) => string.Concat(
        Directory.GetFiles(Path.Combine(root, "Shared", "AgOpenWeb.RemoteWiring"), "*.cs").Select(File.ReadAllText)
        .Concat(Directory.GetFiles(Path.Combine(root, "Shared", "AgOpenWeb.RemoteServer"), "*.cs").Select(File.ReadAllText)));
    private static string ViewModelsAll(string root) => string.Concat(
        Directory.GetFiles(Path.Combine(root, "Shared", "AgOpenWeb.ViewModels"), "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));

    /// <summary>Every command id the client sends: transport.send('id…') / rnSend('id…') / "id…".</summary>
    private static HashSet<string> SentIds(string js)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(js, @"(?:transport\.send|rnSend)\(\s*['""]([a-zA-Z][a-zA-Z0-9_.]*)"))
            ids.Add(m.Groups[1].Value);
        return ids;
    }

    /// <summary>Every handled id: `case "id":` statements, `"id" => …` switch-expression arms,
    /// and prefixes handled via StartsWith.</summary>
    private static (HashSet<string> cases, List<string> prefixes) HandledIds(string wiring)
    {
        var cases = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(wiring, @"case\s+""([a-zA-Z][a-zA-Z0-9_.]*)""\s*:"))
            cases.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(wiring, @"""([a-zA-Z][a-zA-Z0-9_.]*)""\s*=>"))
            cases.Add(m.Groups[1].Value);
        var prefixes = Regex.Matches(wiring, @"(?:cmd|id)\.StartsWith\(""([a-zA-Z0-9_.]+)""\)")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        return (cases, prefixes);
    }

    [Test]
    public void EveryCommandTheClientSends_HasABackendHandler()
    {
        var root = RepoRoot();
        var sent = SentIds(AppJs(root));
        var (cases, prefixes) = HandledIds(WiringAll(root));
        Assert.That(sent, Is.Not.Empty, "sanity: found no transport.send ids in app.js");

        var orphans = sent
            .Where(id => !cases.Contains(id) && !prefixes.Any(p => id.StartsWith(p, StringComparison.Ordinal)))
            .OrderBy(x => x).ToList();

        Assert.That(orphans, Is.Empty,
            "app.js sends these command ids but NO wiring file has a case/prefix handler — the hub " +
            "silently ignores them (button does nothing):\n  " + string.Join("\n  ", orphans));
    }

    [Test]
    public void EveryWiringMapping_ResolvesToAnAssignedViewModelCommand()
    {
        var root = RepoRoot();
        var wiring = WiringAll(root);
        var vms = ViewModelsAll(root);

        // "<id>" => vm.<Name>Command  (and asvm./other receivers)
        var mapped = Regex.Matches(wiring, @"""([a-zA-Z][a-zA-Z0-9_.]*)""\s*=>\s*[a-zA-Z]+\.([A-Za-z0-9_]+Command)\b")
            .Select(m => (id: m.Groups[1].Value, cmd: m.Groups[2].Value)).ToList();
        Assert.That(mapped, Is.Not.Empty, "sanity: found no '=> vm.XCommand' mappings");

        // A command counts as assigned if any VM source does `<Name>Command = new ...` or
        // `<Name>Command = ReactiveCommand...` (not just the property declaration).
        var unassigned = mapped
            .Where(m => !Regex.IsMatch(vms, @"\b" + Regex.Escape(m.cmd) + @"\s*=\s*(new\s|ReactiveCommand|[A-Za-z_]+\.Create)"))
            .Select(m => $"{m.id} => {m.cmd}")
            .Distinct().OrderBy(x => x).ToList();

        Assert.That(unassigned, Is.Empty,
            "these wiring mappings point at a ViewModel command that is never assigned — " +
            "c?.CanExecute(null) on null means the button silently does nothing:\n  " + string.Join("\n  ", unassigned));
    }

    /// <summary>
    /// Layer 0: every HTML button must have SOME JavaScript path. Wiring forms that count:
    ///  - the id literal appears in app.js (getElementById / querySelector / wireRn / template),
    ///  - a data-cmd attribute (the container dispatchers + the document-level fallback
    ///    cover every data-cmd button now),
    ///  - a class app.js dispatches on (.tl-chartbtn, .ln-closex, .rp-pat, .bm-flag …).
    /// Caught today: autosteer "Send+Save" and the five offset-fix D-pad buttons carried
    /// data-cmd but sat outside every container dispatcher — dead for the life of the web UI.
    /// </summary>
    [Test]
    public void EveryHtmlButton_HasAJavaScriptPath()
    {
        var root = RepoRoot();
        var html = File.ReadAllText(Path.Combine(root, "Shared", "AgOpenWeb.RemoteServer", "wwwroot", "index.html"));
        var js = AppJs(root);

        // Classes app.js attaches listeners to (querySelectorAll('.cls') / '.cls[' forms).
        var dispatchedClasses = new HashSet<string>(
            Regex.Matches(js, @"querySelectorAll\(\s*['""][^'""]*?\.([a-zA-Z][a-zA-Z0-9_-]*)")
                 .Select(m => m.Groups[1].Value));

        var dead = new List<string>();
        foreach (Match m in Regex.Matches(html, @"<button([^>]*)id=""([^""]+)""([^>]*)>"))
        {
            string attrs = m.Groups[1].Value + m.Groups[3].Value, id = m.Groups[2].Value;
            bool byId = js.Contains("'" + id + "'") || js.Contains("\"" + id + "\"") || js.Contains("#" + id);
            bool byCmd = attrs.Contains("data-cmd=");
            var classes = Regex.Match(attrs, @"class=""([^""]*)""").Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool byClass = classes.Any(dispatchedClasses.Contains);
            if (!byId && !byCmd && !byClass) dead.Add(id);
        }

        Assert.That(dead, Is.Empty,
            "these HTML buttons have no JavaScript handler path at all (no id reference, no data-cmd, " +
            "no dispatched class) — tapping them does nothing:\n  " + string.Join("\n  ", dead));
    }
}
