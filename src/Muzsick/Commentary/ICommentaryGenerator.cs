// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System.Threading;
using System.Threading.Tasks;
using Muzsick.Metadata;

namespace Muzsick.Commentary;

public interface ICommentaryGenerator
{
	Task<string?> GenerateAsync(TrackInfo track, CancellationToken cancellationToken);
}

