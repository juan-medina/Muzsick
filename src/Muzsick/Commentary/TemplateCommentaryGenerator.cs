// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System.Threading;
using System.Threading.Tasks;
using Muzsick.Config;
using Muzsick.Metadata;

namespace Muzsick.Commentary;

public class TemplateCommentaryGenerator : ICommentaryGenerator
{
	public Task<string?> GenerateAsync(TrackInfo track, CancellationToken cancellationToken)
	{
		var text = AnnouncementTemplateRenderer.Render(App.Settings.AnnouncementTemplate, track);
		return Task.FromResult<string?>(text);
	}
}

