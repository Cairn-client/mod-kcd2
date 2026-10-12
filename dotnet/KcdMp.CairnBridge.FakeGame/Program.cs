// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Runtime.InteropServices;

// <root>\Bin\<config>\KingdomCome.exe, like the Modding Tools build. Beside it: WHGame.dll (loaded, so the launcher sees the game is injectable).
// In <root>: steam_appid.txt, and the kcd.log this writes. The test steers it with flag files in <root>: world.flag (a save loaded), quit.flag (exit),
// and append.txt (its lines are added to kcd.log, like the mod's menu events).
string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
string root = Path.GetFullPath(Path.Combine(exeDir, "..", ".."));
string log = Path.Combine(root, "kcd.log");

NativeLibrary.Load(Path.Combine(exeDir, "WHGame.dll"));
using (var w = new StreamWriter(new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true })
{
    w.WriteLine($"Log Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    w.WriteLine("PlayVideoOnly 'main_menu'");
    var until = DateTime.UtcNow.AddMinutes(3);
    bool world = false;
    string append = Path.Combine(root, "append.txt");
    while (DateTime.UtcNow < until && !File.Exists(Path.Combine(root, "quit.flag")))
    {
        if (!world && File.Exists(Path.Combine(root, "world.flag")))
        {
            world = true;
            w.WriteLine("Loading saved game");
            Thread.Sleep(300);
            w.WriteLine("Gameplay started");
        }
        if (File.Exists(append))
        {
            string add = File.ReadAllText(append);
            File.Delete(append);
            foreach (var l in add.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)) w.WriteLine(l);
        }
        Thread.Sleep(100);
    }
}
