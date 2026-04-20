// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

namespace Muzsick.Commentary;

public enum CommentaryError
{
	None,
	Cancelled,
	Timeout,
	Unreachable,
	Unauthorized,
	RateLimited,
	QuotaExceeded,
	ServerError,
	EmptyResponse,
}


public record CommentaryResult
{
	public string? Text { get; private init; }

	public CommentaryError Error { get; private init; }

	public static CommentaryResult Success(string text) =>
		new() { Text = text, Error = CommentaryError.None };

	public static CommentaryResult Fail(CommentaryError error) =>
		new() { Text = null, Error = error };
}

