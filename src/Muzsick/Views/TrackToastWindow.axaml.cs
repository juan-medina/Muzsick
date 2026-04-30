// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Muzsick.Config;

namespace Muzsick.Views;

public partial class TrackToastWindow : Window
{
	public string TrackTitle { get; }
	public string ArtistName { get; }
	public string? AlbumLine { get; }
	public Bitmap? AlbumArt { get; }

	private readonly TrackNotificationCorner _corner;
	private readonly Action _onClicked;
	private Window? _ownerWindow;
	private bool _closed;

	private const double _slideDuration = 250.0;
	private const double _holdDuration = 3500.0;
	private const double _slideOffset = 340.0;

	private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
	private DateTime _startTime;

	private TranslateTransform? _slideTransform;
	private Rectangle? _progressBarRect;
	private Grid? _progressBarContainer;

	public TrackToastWindow(
		string trackTitle,
		string artistName,
		string? albumLine,
		Bitmap? albumArt,
		TrackNotificationCorner corner,
		Action onClicked)
	{
		TrackTitle = trackTitle;
		ArtistName = artistName;
		AlbumLine = albumLine;
		AlbumArt = albumArt;
		_corner = corner;
		_onClicked = onClicked;

		InitializeComponent();
		DataContext = this;

		_timer.Tick += OnTick;
	}

	public void SetOwnerWindow(Window owner) => _ownerWindow = owner;

	protected override void OnOpened(EventArgs e)
	{
		base.OnOpened(e);
		PositionWindow();

		_slideTransform = this.FindControl<Border>("RootBorder")?.RenderTransform as TranslateTransform;
		_progressBarRect = this.FindControl<Rectangle>("ProgressBarRect");
		_progressBarContainer = this.FindControl<Grid>("ProgressBarContainer");

		_startTime = DateTime.UtcNow;
		_timer.Start();
	}

	private void PositionWindow()
	{
		const int margin = 12;
		var screen = _ownerWindow != null
			? Screens.ScreenFromWindow(_ownerWindow)
			: Screens.Primary;

		if (screen == null) return;

		var area = screen.WorkingArea;
		double x, y;

		switch (_corner)
		{
			case TrackNotificationCorner.BottomRight:
				x = area.X + area.Width - Width - margin;
				y = area.Y + area.Height - Height - margin;
				break;
			case TrackNotificationCorner.BottomLeft:
				x = area.X + margin;
				y = area.Y + area.Height - Height - margin;
				break;
			case TrackNotificationCorner.TopRight:
				x = area.X + area.Width - Width - margin;
				y = area.Y + margin;
				break;
			default:
				x = area.X + margin;
				y = area.Y + margin;
				break;
		}

		Position = new PixelPoint((int)x, (int)y);
	}

	private void OnTick(object? sender, EventArgs e)
	{
		if (_closed) return;

		var elapsed = (DateTime.UtcNow - _startTime).TotalMilliseconds;
		var isRightSide = _corner is TrackNotificationCorner.BottomRight or TrackNotificationCorner.TopRight;
		var slideDir = isRightSide ? _slideOffset : -_slideOffset;

		if (elapsed <= _slideDuration)
		{
			var t = elapsed / _slideDuration;
			var eased = 1.0 - Math.Pow(1.0 - t, 3.0);
			if (_slideTransform != null)
				_slideTransform.X = slideDir * (1.0 - eased);
		}
		else if (elapsed <= _slideDuration + _holdDuration)
		{
			if (_slideTransform != null)
				_slideTransform.X = 0;

			var holdElapsed = elapsed - _slideDuration;
			var fraction = 1.0 - holdElapsed / _holdDuration;
			var containerWidth = _progressBarContainer?.Bounds.Width ?? 0;
			if (containerWidth > 0 && _progressBarRect != null)
				_progressBarRect.Width = containerWidth * fraction;
		}
		else if (elapsed <= _slideDuration + _holdDuration + _slideDuration)
		{
			var slideElapsed = elapsed - _slideDuration - _holdDuration;
			var t = slideElapsed / _slideDuration;
			var eased = Math.Pow(t, 3.0);
			if (_slideTransform != null)
				_slideTransform.X = slideDir * eased;
		}
		else
		{
			_timer.Stop();
			if (!_closed)
			{
				_closed = true;
				Close();
			}
		}
	}

	private void RootBorder_PointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (_closed) return;
		_closed = true;
		_timer.Stop();
		_onClicked();
		Close();
	}
}
