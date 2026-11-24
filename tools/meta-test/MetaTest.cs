// Offline test for mod/src/SavePointMeta.cs (rename, pin, delete). Runs on real temp folders, no mocks.
// Run: tools/meta-test/run.sh
using System;
using System.IO;
using System.Text;
using TygMod;

internal static class MetaTest
{
    private static int _fail, _pass;

    private static void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else
        {
            _fail++;
            Console.WriteLine("FAIL: " + what);
        }
    }

    private static void Throws<T>(Action a, string what) where T : Exception
    {
        try
        {
            a();
            Check(false, what + " (no exception)");
        }
        catch (T)
        {
            Check(true, what);
        }
        catch (Exception e)
        {
            Check(false, what + " (wrong exception " + e.GetType().Name + ")");
        }
    }

    private static string MakeSp(string root, string name)
    {
        string d = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(d, "Trust Your Gut"));
        File.WriteAllText(Path.Combine(d, "Trust Your Gut", "Save_Game.es3"), "x");
        return d;
    }

    private static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "tyg-meta-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // ---- Clean ----
            Check(SavePointMeta.Clean(null) == "", "clean null");
            Check(SavePointMeta.Clean("  a \t b\r\n c  ") == "a b c", "clean collapses spaces and line breaks");
            Check(SavePointMeta.Clean("﻿boss") == "boss", "clean drops byte order mark");
            Check(SavePointMeta.Clean(new string('x', 80)).Length == SavePointMeta.MaxLabelLength, "clean caps length");
            string emoji = new string('a', SavePointMeta.MaxLabelLength - 1) + "\U0001F600"; // pair would cross the cap
            string ce = SavePointMeta.Clean(emoji);
            Check(ce.Length == SavePointMeta.MaxLabelLength - 1 && !char.IsHighSurrogate(ce[ce.Length - 1]), "clean does not split a two-part character");

            // ---- name ----
            string a = MakeSp(root, "sp-20261123-000149");
            Check(SavePointMeta.ReadLabel(a) == null, "no label file = no name");
            Check(SavePointMeta.SetLabel(root, a, "  All in on black ") == "All in on black", "set label returns clean name");
            Check(SavePointMeta.ReadLabel(a) == "All in on black", "label round trip");
            SavePointMeta.SetLabel(root, a, "Boss day 11 – Ünïcode");
            Check(SavePointMeta.ReadLabel(a) == "Boss day 11 – Ünïcode", "label replace + unicode");
            Check(!File.Exists(Path.Combine(a, "label.txt.tmp")), "no temp file left");
            Check(SavePointMeta.SetLabel(root, a, "   ") == null && !File.Exists(Path.Combine(a, "label.txt")), "empty name removes file");
            Check(!File.Exists(Path.Combine(a, "Trust Your Gut", "label.txt")), "label never inside the data folder");

            // ---- pin ----
            string b = MakeSp(root, "sp-20261123-000744");
            string c = MakeSp(root, "auto-20261123-000220");
            string d = MakeSp(root, "auto-20261123-000725");
            string e = MakeSp(root, "live-20261122-225043");
            Directory.CreateDirectory(Path.Combine(root, ".trash", "old"));
            File.WriteAllText(Path.Combine(root, ".trash", "old", "pin.txt"), "1"); // must not count (inside a dot folder)
            File.WriteAllText(Path.Combine(root, ".hidden-pin.txt"), "1");
            Check(SavePointMeta.Pin(root, a) == 1, "first pin = slot 1");
            Check(SavePointMeta.Pin(root, b) == 2, "second pin = slot 2");
            Check(SavePointMeta.Pin(root, a) == 1, "pin again keeps slot");
            Check(SavePointMeta.Pin(root, c) == 3, "third pin = slot 3");
            Check(SavePointMeta.Pin(root, d) == 4, "fourth pin = slot 4");
            Check(SavePointMeta.Pin(root, e) == 0 && SavePointMeta.ReadPin(e) == 0, "fifth pin refused");
            SavePointMeta.Unpin(root, b);
            Check(SavePointMeta.ReadPin(b) == 0, "unpin clears");
            Check(SavePointMeta.Pin(root, e) == 2, "freed slot is reused");
            File.WriteAllText(Path.Combine(b, "pin.txt"), "9");
            Check(SavePointMeta.ReadPin(b) == 0, "slot out of range = not pinned");
            File.WriteAllText(Path.Combine(b, "pin.txt"), "abc");
            Check(SavePointMeta.ReadPin(b) == 0, "bad pin file = not pinned");
            File.Delete(Path.Combine(b, "pin.txt"));
            SavePointMeta.Unpin(root, b); // no file: no error
            Check(true, "unpin without pin is fine");

            // ---- delete ----
            Throws<InvalidOperationException>(() => SavePointMeta.MoveToTrash(root, a), "pinned save point cannot be deleted");
            Check(Directory.Exists(a), "pinned folder still there");
            string moved = SavePointMeta.MoveToTrash(root, b);
            Check(!Directory.Exists(b) && Directory.Exists(moved) && File.Exists(Path.Combine(moved, "Trust Your Gut", "Save_Game.es3")),
                  "delete moves the whole folder to .trash");
            Check(Path.GetDirectoryName(moved) == Path.Combine(root, ".trash"), "moved into root/.trash");
            string b2 = MakeSp(root, "sp-20261123-000744");
            string moved2 = SavePointMeta.MoveToTrash(root, b2);
            Check(moved2.EndsWith("sp-20261123-000744-2") && Directory.Exists(moved) && Directory.Exists(moved2), "same name in trash gets -2, old one kept");

            // ---- folder checks ----
            Throws<ArgumentException>(() => SavePointMeta.SetLabel(root, root, "x"), "root itself refused");
            Throws<ArgumentException>(() => SavePointMeta.SetLabel(root, Path.Combine(a, "Trust Your Gut"), "x"), "nested folder refused");
            Throws<ArgumentException>(() => SavePointMeta.MoveToTrash(root, Path.Combine(root, ".trash")), "dot folder refused");
            Throws<ArgumentException>(() => SavePointMeta.MoveToTrash(root, Path.Combine(root, "..", "elsewhere")), "outside root refused");
            Throws<ArgumentException>(() => SavePointMeta.MoveToTrash(root, Path.GetTempPath()), "other folder refused");
            Throws<DirectoryNotFoundException>(() => SavePointMeta.Pin(root, Path.Combine(root, "sp-gone")), "missing folder refused");
            Throws<ArgumentException>(() => SavePointMeta.Pin(root, ""), "empty path refused");
            Check(SavePointMeta.SetLabel(root + Path.DirectorySeparatorChar, a, "slash ok") == "slash ok", "root with end slash works");
            string f = MakeSp(root, "sp-20261123-010000");
            string viaDots = Path.Combine(root, "board-proof", "..", "sp-20261123-010000");
            string movedF = SavePointMeta.MoveToTrash(root, viaDots);
            Check(Path.GetFileName(movedF) == "sp-20261123-010000" && !Directory.Exists(f), "a '..' path moves the right folder under its real name");

            // ---- inside check (guards the live save folder) ----
            Check(SavePointMeta.IsSameOrInside(Path.Combine(root, "x", "y"), root), "child is inside");
            Check(SavePointMeta.IsSameOrInside(root, root + Path.DirectorySeparatorChar), "same folder counts");
            Check(!SavePointMeta.IsSameOrInside(root + "-other", root), "name prefix is not inside");
            Check(!SavePointMeta.IsSameOrInside(root, Path.Combine(root, "x")), "parent is not inside child");
            Check(!SavePointMeta.IsSameOrInside("", root), "empty is not inside");
        }
        finally
        {
            Directory.Delete(root, true);
        }
        DetailsTest.Run(args.Length > 0 ? args[0] : null, Check);
        Console.WriteLine("passed " + _pass + ", failed " + _fail);
        return _fail == 0 ? 0 : 1;
    }
}
