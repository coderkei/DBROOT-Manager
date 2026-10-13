// SPDX-FileCopyrightText: 2026 coderkei
// SPDX-License-Identifier: GPL-3.0-or-later

namespace DbRoot;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.FirstOrDefault()));
    }
}
