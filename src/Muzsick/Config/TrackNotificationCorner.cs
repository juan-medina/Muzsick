// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Muzsick.Config;

public enum TrackNotificationCorner
{
	BottomRight,
	BottomLeft,
	TopRight,
	TopLeft,
}

public class TrackNotificationCornerConverter : IValueConverter
{
	public static readonly TrackNotificationCornerConverter Instance = new();

	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
		value is TrackNotificationCorner corner ? corner switch
		{
			TrackNotificationCorner.BottomRight => "Bottom right",
			TrackNotificationCorner.BottomLeft => "Bottom left",
			TrackNotificationCorner.TopRight => "Top right",
			TrackNotificationCorner.TopLeft => "Top left",
			_ => value.ToString(),
		} : value;

	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}
