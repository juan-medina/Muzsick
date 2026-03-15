// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Muzsick.Update;

internal sealed class UpdateService
{
	private const string _repoUrl = "https://github.com/juan-medina/Muzsick";

	/// <summary>
	/// Checks GitHub Releases for a newer version. If one is found, downloads and stages it
	/// silently. The caller is responsible for notifying the user to restart.
	/// Returns true if an update was staged, false otherwise (including on failure).
	/// </summary>
	public async Task<bool> CheckAndApplyUpdatesAsync(ILogger? logger = null)
	{
		try
		{
			var mgr = new UpdateManager(new GithubSource(_repoUrl, null, false));

			// Skip the check when running from a dev build / unpacked output.
			if (!mgr.IsInstalled)
			{
				logger?.LogDebug("App is not installed via Velopack; skipping update check.");
				return false;
			}

			logger?.LogInformation("Checking for updates...");

			var updateInfo = await mgr.CheckForUpdatesAsync();
			if (updateInfo == null)
			{
				logger?.LogInformation("No updates available.");
				return false;
			}

			logger?.LogInformation("Update available: {Version} — downloading in background.",
				updateInfo.TargetFullRelease.Version);

			await mgr.DownloadUpdatesAsync(updateInfo);

			logger?.LogInformation("Update staged. It will be applied on next restart.");
			return true;
		}
		catch (Exception ex)
		{
			logger?.LogWarning(ex, "Update check failed; the app will continue normally.");
			return false;
		}
	}
}

