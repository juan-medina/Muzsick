﻿// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using Avalonia;
using System;
using Microsoft.Extensions.Logging;

namespace Muzsick;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
#if DEBUG
        // Set up console logging for debug builds only with timestamps
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole(options =>
            {
                options.FormatterName = "simple";
            })
            .AddSimpleConsole(options =>
            {
                options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss.fff] ";
                options.UseUtcTimestamp = false;
            })
            .SetMinimumLevel(LogLevel.Debug);
        });

        // Store logger factory globally for DI
        App.LoggerFactory = loggerFactory;
#endif

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
