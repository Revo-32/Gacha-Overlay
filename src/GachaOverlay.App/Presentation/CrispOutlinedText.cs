using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using GachaOverlay.Core.Chat;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;

namespace GachaOverlay.App.Presentation;

public sealed class CrispOutlinedText : System.Windows.Controls.Control
{
    private readonly HashSet<ChatTokenViewModel> _subscribedTokens = new();
    private INotifyCollectionChanged? _observableTokens;
    private UnifiedTextLayout? _layout;
    private LayoutKey? _layoutKey;
    private int _contentRevision;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(CrispOutlinedText),
        LayoutMetadata(string.Empty));
    public static readonly DependencyProperty TokensProperty = DependencyProperty.Register(
        nameof(Tokens),
        typeof(IEnumerable<ChatTokenViewModel>),
        typeof(CrispOutlinedText),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsMeasure |
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnTokensChanged));
    public static readonly DependencyProperty EmojiExtentProperty = DependencyProperty.Register(
        nameof(EmojiExtent),
        typeof(double),
        typeof(CrispOutlinedText),
        LayoutMetadata(18d));
    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
        nameof(TextWrapping),
        typeof(TextWrapping),
        typeof(CrispOutlinedText),
        LayoutMetadata(TextWrapping.NoWrap));
    public static readonly DependencyProperty TextTrimmingProperty = DependencyProperty.Register(
        nameof(TextTrimming),
        typeof(TextTrimming),
        typeof(CrispOutlinedText),
        LayoutMetadata(TextTrimming.None));
    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
        nameof(TextAlignment),
        typeof(TextAlignment),
        typeof(CrispOutlinedText),
        LayoutMetadata(TextAlignment.Left));
    public static readonly DependencyProperty LineHeightProperty = DependencyProperty.Register(
        nameof(LineHeight),
        typeof(double),
        typeof(CrispOutlinedText),
        LayoutMetadata(double.NaN));
    public static readonly DependencyProperty OutlineEnabledProperty = DependencyProperty.Register(
        nameof(OutlineEnabled),
        typeof(bool),
        typeof(CrispOutlinedText),
        PaintMetadata(true));
    public static readonly DependencyProperty OutlineBrushProperty = DependencyProperty.Register(
        nameof(OutlineBrush),
        typeof(Brush),
        typeof(CrispOutlinedText),
        PaintMetadata(null));
    public static readonly DependencyProperty OutlineThicknessProperty = DependencyProperty.Register(
        nameof(OutlineThickness),
        typeof(double),
        typeof(CrispOutlinedText),
        new FrameworkPropertyMetadata(
            1.5d,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnPaintPropertyChanged,
            CoerceOutlineThickness));
    public static readonly DependencyProperty MentionForegroundProperty = DependencyProperty.Register(
        nameof(MentionForeground),
        typeof(Brush),
        typeof(CrispOutlinedText),
        PaintMetadata(null));
    public static readonly DependencyProperty SelfMentionForegroundProperty = DependencyProperty.Register(
        nameof(SelfMentionForeground),
        typeof(Brush),
        typeof(CrispOutlinedText),
        PaintMetadata(null));
    public static readonly DependencyProperty MentionBackgroundProperty = DependencyProperty.Register(
        nameof(MentionBackground),
        typeof(Brush),
        typeof(CrispOutlinedText),
        PaintMetadata(null));
    public static readonly DependencyProperty SelfMentionBackgroundProperty = DependencyProperty.Register(
        nameof(SelfMentionBackground),
        typeof(Brush),
        typeof(CrispOutlinedText),
        PaintMetadata(null));

    public CrispOutlinedText()
    {
        ClipToBounds = false;
        Unloaded += (_, _) => ClearLayout();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IEnumerable<ChatTokenViewModel>? Tokens
    {
        get => (IEnumerable<ChatTokenViewModel>?)GetValue(TokensProperty);
        set => SetValue(TokensProperty, value);
    }

    public double EmojiExtent
    {
        get => (double)GetValue(EmojiExtentProperty);
        set => SetValue(EmojiExtentProperty, value);
    }

    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    public TextTrimming TextTrimming
    {
        get => (TextTrimming)GetValue(TextTrimmingProperty);
        set => SetValue(TextTrimmingProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public double LineHeight
    {
        get => (double)GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    public bool OutlineEnabled
    {
        get => (bool)GetValue(OutlineEnabledProperty);
        set => SetValue(OutlineEnabledProperty, value);
    }

    public Brush? OutlineBrush
    {
        get => (Brush?)GetValue(OutlineBrushProperty);
        set => SetValue(OutlineBrushProperty, value);
    }

    public double OutlineThickness
    {
        get => (double)GetValue(OutlineThicknessProperty);
        set => SetValue(OutlineThicknessProperty, value);
    }

    public Brush? MentionForeground
    {
        get => (Brush?)GetValue(MentionForegroundProperty);
        set => SetValue(MentionForegroundProperty, value);
    }

    public Brush? SelfMentionForeground
    {
        get => (Brush?)GetValue(SelfMentionForegroundProperty);
        set => SetValue(SelfMentionForegroundProperty, value);
    }

    public Brush? MentionBackground
    {
        get => (Brush?)GetValue(MentionBackgroundProperty);
        set => SetValue(MentionBackgroundProperty, value);
    }

    public Brush? SelfMentionBackground
    {
        get => (Brush?)GetValue(SelfMentionBackgroundProperty);
        set => SetValue(SelfMentionBackgroundProperty, value);
    }

    internal int LayoutBuildCount { get; private set; }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size constraint)
    {
        var layout = GetLayout(
            NormalizeWidth(constraint.Width),
            NormalizeHeight(constraint.Height));
        return new System.Windows.Size(
            double.IsPositiveInfinity(constraint.Width)
                ? layout.Width
                : Math.Min(constraint.Width, layout.Width),
            double.IsPositiveInfinity(constraint.Height)
                ? layout.Height
                : Math.Min(constraint.Height, layout.Height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var layout = GetLayout(
            NormalizeWidth(RenderSize.Width),
            NormalizeHeight(RenderSize.Height));
        var outlinePen = CreateOutlinePen();
        var y = 0d;
        foreach (var line in layout.Lines)
        {
            DrawMentionBackgrounds(drawingContext, layout.Content, line, y);
            foreach (var indexedRun in line.Line.GetIndexedGlyphRuns())
            {
                var glyphRun = indexedRun.GlyphRun;
                var geometry = glyphRun.BuildGeometry();
                var desiredBaselineX = line.Line.Start +
                    line.Line.GetDistanceFromCharacterHit(
                        new CharacterHit(indexedRun.TextSourceCharacterIndex, 0));
                var desiredBaselineY = y + line.Line.Baseline;
                drawingContext.PushTransform(new TranslateTransform(
                    desiredBaselineX - glyphRun.BaselineOrigin.X,
                    desiredBaselineY - glyphRun.BaselineOrigin.Y));
                if (OutlineEnabled && outlinePen is not null)
                {
                    drawingContext.DrawGeometry(null, outlinePen, geometry);
                }

                drawingContext.DrawGeometry(
                    ResolveForeground(layout.Content.FindSegment(indexedRun.TextSourceCharacterIndex)),
                    null,
                    geometry);
                drawingContext.Pop();
            }

            line.Line.Draw(
                drawingContext,
                new System.Windows.Point(0, y),
                InvertAxes.None);
            y += line.Line.Height;
        }
    }

    private UnifiedTextLayout GetLayout(double availableWidth, double availableHeight)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var content = BuildContent();
        var key = new LayoutKey(
            _contentRevision,
            content.Text,
            CultureInfo.CurrentUICulture.Name,
            FontFamily.Source,
            FontStyle,
            FontWeight,
            FontStretch,
            FontSize,
            LineHeight,
            availableWidth,
            availableHeight,
            TextWrapping,
            TextTrimming,
            TextAlignment,
            FlowDirection,
            dpi,
            TextOptions.GetTextFormattingMode(this));
        if (_layoutKey == key && _layout is not null)
        {
            return _layout;
        }

        ClearLayout();
        _layout = BuildLayout(content, key);
        _layoutKey = key;
        LayoutBuildCount++;
        return _layout;
    }

    private UnifiedTextLayout BuildLayout(UnifiedTextContent content, LayoutKey key)
    {
        if (content.Text.Length == 0 || FontSize <= 0)
        {
            return new UnifiedTextLayout(content, Array.Empty<LayoutLine>(), 0, 0, null);
        }

        var paragraphLineHeight = double.IsFinite(LineHeight) && LineHeight > 0
            ? LineHeight
            : 0;
        var effectiveEmojiExtent = double.IsFinite(EmojiExtent) && EmojiExtent > 0
            ? EmojiExtent
            : 18;
        if (paragraphLineHeight > 0)
        {
            effectiveEmojiExtent = Math.Min(effectiveEmojiExtent, paragraphLineHeight);
        }

        var defaultProperties = CreateRunProperties(TextSegmentStyle.Normal, key.PixelsPerDip);
        var source = new UnifiedTextSource(
            content,
            style => CreateRunProperties(style, key.PixelsPerDip),
            effectiveEmojiExtent);
        var regularParagraph = new UnifiedParagraphProperties(
            defaultProperties,
            FlowDirection,
            TextAlignment,
            paragraphLineHeight,
            TextWrapping);
        var noWrapParagraph = new UnifiedParagraphProperties(
            defaultProperties,
            FlowDirection,
            TextAlignment,
            paragraphLineHeight,
            TextWrapping.NoWrap);
        var effectiveLineHeight = double.IsNaN(LineHeight) || LineHeight <= 0
            ? Math.Max(1, FontSize * 1.2)
            : LineHeight;
        var maxLines = double.IsPositiveInfinity(key.AvailableHeight)
            ? int.MaxValue
            : Math.Max(1, (int)Math.Floor(key.AvailableHeight / effectiveLineHeight));
        var lines = new List<LayoutLine>();
        var textPosition = 0;
        var width = 0d;
        var height = 0d;
        TextLineBreak? previousBreak = null;
        var formatter = TextFormatter.Create(key.FormattingMode);
        try
        {
            try
            {
                while (textPosition < content.Text.Length && lines.Count < maxLines)
                {
                    var isLastVisibleLine = lines.Count == maxLines - 1;
                    var paragraph = isLastVisibleLine && TextWrapping != TextWrapping.NoWrap
                        ? noWrapParagraph
                        : regularParagraph;
                    var formatted = formatter.FormatLine(
                        source,
                        textPosition,
                        Math.Max(0.01, key.AvailableWidth),
                        paragraph,
                        previousBreak);
                    previousBreak?.Dispose();
                    previousBreak = formatted.GetTextLineBreak();
                    var sourceLength = formatted.Length;
                    var hasRemainingText = textPosition + sourceLength < content.Text.Length;
                    var shouldCollapse = TextTrimming != TextTrimming.None &&
                        (formatted.HasOverflowed || isLastVisibleLine && hasRemainingText);
                    TextLine visibleLine = formatted;
                    if (shouldCollapse)
                    {
                        visibleLine = formatted.Collapse(
                        [
                            new TextTrailingCharacterEllipsis(
                                Math.Max(0.01, key.AvailableWidth),
                                defaultProperties),
                        ]);
                        if (!ReferenceEquals(visibleLine, formatted))
                        {
                            formatted.Dispose();
                        }
                    }

                    lines.Add(new LayoutLine(visibleLine, textPosition));
                    width = Math.Max(width, visibleLine.WidthIncludingTrailingWhitespace);
                    height += visibleLine.Height;
                    textPosition += Math.Max(1, sourceLength);
                    if (paragraph.TextWrapping == TextWrapping.NoWrap)
                    {
                        break;
                    }
                }
            }
            finally
            {
                previousBreak?.Dispose();
            }

            return new UnifiedTextLayout(content, lines, width, height, formatter);
        }
        catch
        {
            foreach (var line in lines)
            {
                line.Line.Dispose();
            }
            formatter.Dispose();
            throw;
        }
    }

    private UnifiedTextContent BuildContent()
    {
        var segments = new List<TextSegment>();
        var text = new System.Text.StringBuilder();
        if (Tokens is null)
        {
            var value = Text ?? string.Empty;
            segments.Add(new TextSegment(0, value.Length, TextSegmentStyle.Normal, null));
            return new UnifiedTextContent(value, segments);
        }

        foreach (var token in Tokens)
        {
            var start = text.Length;
            if (token.Kind == ChatTokenKind.CustomEmoji && token.Image is not null)
            {
                text.Append('\uFFFC');
                segments.Add(new TextSegment(
                    start,
                    1,
                    TextSegmentStyle.Emoji,
                    token.Image));
                continue;
            }

            text.Append(token.Text);
            var style = token.Kind == ChatTokenKind.Mention
                ? token.IsSelfMention
                    ? TextSegmentStyle.SelfMention
                    : TextSegmentStyle.Mention
                : TextSegmentStyle.Normal;
            segments.Add(new TextSegment(start, token.Text.Length, style, null));
        }

        return new UnifiedTextContent(text.ToString(), segments);
    }

    private UnifiedTextRunProperties CreateRunProperties(
        TextSegmentStyle style,
        double pixelsPerDip)
    {
        var weight = style switch
        {
            TextSegmentStyle.SelfMention => FontWeights.Bold,
            TextSegmentStyle.Mention => FontWeights.SemiBold,
            _ => FontWeight,
        };
        return new UnifiedTextRunProperties(
            new Typeface(FontFamily, FontStyle, weight, FontStretch),
            FontSize,
            CultureInfo.CurrentUICulture,
            pixelsPerDip);
    }

    private Brush ResolveForeground(TextSegment segment) => segment.Style switch
    {
        TextSegmentStyle.Mention => MentionForeground ?? Foreground,
        TextSegmentStyle.SelfMention => SelfMentionForeground ?? Foreground,
        _ => Foreground,
    };

    private Brush? ResolveBackground(TextSegment segment) => segment.Style switch
    {
        TextSegmentStyle.Mention => MentionBackground,
        TextSegmentStyle.SelfMention => SelfMentionBackground,
        _ => null,
    };

    private void DrawMentionBackgrounds(
        DrawingContext drawingContext,
        UnifiedTextContent content,
        LayoutLine layoutLine,
        double y)
    {
        var lineStart = layoutLine.TextPosition;
        var lineEnd = lineStart + layoutLine.Line.Length;
        foreach (var segment in content.Segments.Where(segment =>
                     segment.Style is TextSegmentStyle.Mention or TextSegmentStyle.SelfMention))
        {
            var background = ResolveBackground(segment);
            if (background is null)
            {
                continue;
            }

            var start = Math.Max(segment.Start, lineStart);
            var end = Math.Min(segment.Start + segment.Length, lineEnd);
            if (end <= start)
            {
                continue;
            }

            foreach (var bounds in layoutLine.Line.GetTextBounds(start, end - start))
            {
                var rectangle = bounds.Rectangle;
                rectangle.Offset(0, y);
                drawingContext.DrawRectangle(background, null, rectangle);
            }
        }
    }

    private Pen? CreateOutlinePen()
    {
        if (!OutlineEnabled || OutlineBrush is null || OutlineThickness <= 0)
        {
            return null;
        }

        var pen = new Pen(OutlineBrush, OutlineThickness * 2)
        {
            LineJoin = PenLineJoin.Round,
        };
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        return pen;
    }

    private void InvalidateTextLayout()
    {
        _contentRevision++;
        ClearLayout();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void ClearLayout()
    {
        _layout?.Dispose();
        _layout = null;
        _layoutKey = null;
    }

    private static FrameworkPropertyMetadata LayoutMetadata(object? defaultValue) =>
        new(
            defaultValue,
            FrameworkPropertyMetadataOptions.AffectsMeasure |
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnLayoutPropertyChanged);

    private static FrameworkPropertyMetadata PaintMetadata(object? defaultValue) =>
        new(
            defaultValue,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnPaintPropertyChanged);

    private static void OnLayoutPropertyChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args) =>
        ((CrispOutlinedText)sender).InvalidateTextLayout();

    private static void OnPaintPropertyChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args) =>
        ((CrispOutlinedText)sender).InvalidateVisual();

    private static object CoerceOutlineThickness(DependencyObject sender, object value) =>
        Math.Clamp(double.IsFinite((double)value) ? (double)value : 0, 0, 10);

    private static void OnTokensChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        var control = (CrispOutlinedText)sender;
        control.Detach(args.OldValue as IEnumerable<ChatTokenViewModel>);
        control.Attach(args.NewValue as IEnumerable<ChatTokenViewModel>);
        control.InvalidateTextLayout();
    }

    private void Attach(IEnumerable<ChatTokenViewModel>? tokens)
    {
        _observableTokens = tokens as INotifyCollectionChanged;
        if (_observableTokens is not null)
        {
            _observableTokens.CollectionChanged += OnTokenCollectionChanged;
        }

        if (tokens is not null)
        {
            foreach (var token in tokens)
            {
                Subscribe(token);
            }
        }
    }

    private void Detach(IEnumerable<ChatTokenViewModel>? tokens)
    {
        if (_observableTokens is not null)
        {
            _observableTokens.CollectionChanged -= OnTokenCollectionChanged;
            _observableTokens = null;
        }

        foreach (var token in _subscribedTokens.ToArray())
        {
            token.PropertyChanged -= OnTokenPropertyChanged;
        }
        _subscribedTokens.Clear();
    }

    private void OnTokenCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        Detach(Tokens);
        Attach(Tokens);
        InvalidateTextLayout();
    }

    private void Subscribe(ChatTokenViewModel token)
    {
        if (_subscribedTokens.Add(token))
        {
            token.PropertyChanged += OnTokenPropertyChanged;
        }
    }

    private void OnTokenPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ChatTokenViewModel.Image))
        {
            InvalidateTextLayout();
        }
    }

    private static double NormalizeWidth(double width) =>
        double.IsFinite(width) ? Math.Max(0.01, width) : 1_000_000;

    private static double NormalizeHeight(double height) =>
        double.IsFinite(height) ? Math.Max(0.01, height) : double.PositiveInfinity;

    private sealed record LayoutKey(
        int ContentRevision,
        string Text,
        string CultureName,
        string FontFamily,
        System.Windows.FontStyle FontStyle,
        FontWeight FontWeight,
        FontStretch FontStretch,
        double FontSize,
        double LineHeight,
        double AvailableWidth,
        double AvailableHeight,
        TextWrapping Wrapping,
        TextTrimming Trimming,
        TextAlignment Alignment,
        System.Windows.FlowDirection FlowDirection,
        double PixelsPerDip,
        TextFormattingMode FormattingMode);

    private sealed class UnifiedTextLayout : IDisposable
    {
        public UnifiedTextLayout(
            UnifiedTextContent content,
            IReadOnlyList<LayoutLine> lines,
            double width,
            double height,
            TextFormatter? formatter)
        {
            Content = content;
            Lines = lines;
            Width = width;
            Height = height;
            Formatter = formatter;
        }

        public UnifiedTextContent Content { get; }
        public IReadOnlyList<LayoutLine> Lines { get; }
        public double Width { get; }
        public double Height { get; }
        private TextFormatter? Formatter { get; }

        public void Dispose()
        {
            foreach (var line in Lines)
            {
                line.Line.Dispose();
            }

            Formatter?.Dispose();
        }
    }

    private sealed record LayoutLine(TextLine Line, int TextPosition);

    private sealed class UnifiedTextContent
    {
        public UnifiedTextContent(string text, IReadOnlyList<TextSegment> segments)
        {
            Text = text;
            Segments = segments;
        }

        public string Text { get; }
        public IReadOnlyList<TextSegment> Segments { get; }

        public TextSegment FindSegment(int index) => Segments.FirstOrDefault(segment =>
                index >= segment.Start && index < segment.Start + segment.Length)
            ?? Segments.LastOrDefault()
            ?? new TextSegment(0, 0, TextSegmentStyle.Normal, null);
    }

    private sealed record TextSegment(
        int Start,
        int Length,
        TextSegmentStyle Style,
        ImageSource? Image);

    private enum TextSegmentStyle
    {
        Normal,
        Mention,
        SelfMention,
        Emoji,
    }

    private sealed class UnifiedTextSource : TextSource
    {
        private readonly UnifiedTextContent _content;
        private readonly Func<TextSegmentStyle, UnifiedTextRunProperties> _getProperties;
        private readonly double _emojiExtent;

        public UnifiedTextSource(
            UnifiedTextContent content,
            Func<TextSegmentStyle, UnifiedTextRunProperties> getProperties,
            double emojiExtent)
        {
            _content = content;
            _getProperties = getProperties;
            _emojiExtent = emojiExtent;
        }

        public override TextRun GetTextRun(int textSourceCharacterIndex)
        {
            if (textSourceCharacterIndex >= _content.Text.Length)
            {
                return new TextEndOfParagraph(1);
            }

            var segment = _content.FindSegment(textSourceCharacterIndex);
            var properties = _getProperties(segment.Style);
            if (segment.Style == TextSegmentStyle.Emoji && segment.Image is not null)
            {
                return new EmojiEmbeddedObject(
                    _content.Text,
                    textSourceCharacterIndex,
                    segment.Image,
                    properties,
                    _emojiExtent);
            }

            var length = Math.Max(
                1,
                Math.Min(
                    segment.Start + segment.Length - textSourceCharacterIndex,
                    _content.Text.Length - textSourceCharacterIndex));
            return new TextCharacters(
                _content.Text,
                textSourceCharacterIndex,
                length,
                properties);
        }

        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(
            int textSourceCharacterIndexLimit)
        {
            var length = Math.Clamp(textSourceCharacterIndexLimit, 0, _content.Text.Length);
            return new TextSpan<CultureSpecificCharacterBufferRange>(
                length,
                new CultureSpecificCharacterBufferRange(
                    CultureInfo.CurrentUICulture,
                    new CharacterBufferRange(_content.Text, 0, length)));
        }

        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(
            int textSourceCharacterIndex) => textSourceCharacterIndex;
    }

    private sealed class UnifiedTextRunProperties : TextRunProperties
    {
        public UnifiedTextRunProperties(
            Typeface typeface,
            double emSize,
            CultureInfo culture,
            double pixelsPerDip)
        {
            Typeface = typeface;
            FontRenderingEmSize = emSize;
            FontHintingEmSize = emSize;
            CultureInfo = culture;
            PixelsPerDip = pixelsPerDip;
        }

        public override Typeface Typeface { get; }
        public override double FontRenderingEmSize { get; }
        public override double FontHintingEmSize { get; }
        public override TextDecorationCollection? TextDecorations => null;
        public override Brush ForegroundBrush => System.Windows.Media.Brushes.Transparent;
        public override Brush? BackgroundBrush => null;
        public override CultureInfo CultureInfo { get; }
        public override TextEffectCollection? TextEffects => null;
    }

    private sealed class UnifiedParagraphProperties : TextParagraphProperties
    {
        public UnifiedParagraphProperties(
            TextRunProperties defaultProperties,
            System.Windows.FlowDirection flowDirection,
            TextAlignment textAlignment,
            double lineHeight,
            TextWrapping textWrapping)
        {
            DefaultTextRunProperties = defaultProperties;
            FlowDirection = flowDirection;
            TextAlignment = textAlignment;
            LineHeight = lineHeight;
            TextWrapping = textWrapping;
        }

        public override System.Windows.FlowDirection FlowDirection { get; }
        public override TextAlignment TextAlignment { get; }
        public override double LineHeight { get; }
        public override bool FirstLineInParagraph => true;
        public override TextRunProperties DefaultTextRunProperties { get; }
        public override TextWrapping TextWrapping { get; }
        public override TextMarkerProperties? TextMarkerProperties => null;
        public override double Indent => 0;
    }

    private sealed class EmojiEmbeddedObject : TextEmbeddedObject
    {
        private readonly CharacterBufferReference _reference;
        private readonly ImageSource _image;
        private readonly double _width;
        private readonly double _height;
        private readonly double _baseline;

        public EmojiEmbeddedObject(
            string text,
            int index,
            ImageSource image,
            TextRunProperties properties,
            double emojiExtent)
        {
            _reference = new CharacterBufferReference(text, index);
            _image = image;
            Properties = properties;
            _height = Math.Clamp(double.IsFinite(emojiExtent) ? emojiExtent : 18, 1, 48);
            var ratio = double.IsFinite(image.Width) && double.IsFinite(image.Height) &&
                image.Width > 0 && image.Height > 0
                ? image.Width / image.Height
                : 1;
            _width = Math.Clamp(_height * ratio, _height, _height * 1.5) + 4;
            _baseline = _height * 0.82;
        }

        public override LineBreakCondition BreakBefore => LineBreakCondition.BreakPossible;
        public override LineBreakCondition BreakAfter => LineBreakCondition.BreakPossible;
        public override bool HasFixedSize => true;
        public override CharacterBufferReference CharacterBufferReference => _reference;
        public override int Length => 1;
        public override TextRunProperties Properties { get; }

        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) =>
            new(_width, _height, _baseline);

        public override System.Windows.Rect ComputeBoundingBox(bool rightToLeft, bool sideways) =>
            sideways
                ? new System.Windows.Rect(0, -_width, _height, _width)
                : new System.Windows.Rect(0, -_baseline, _width, _height);

        public override void Draw(
            DrawingContext drawingContext,
            System.Windows.Point origin,
            bool rightToLeft,
            bool sideways) =>
            drawingContext.DrawImage(
                _image,
                new System.Windows.Rect(
                    rightToLeft ? origin.X - _width + 2 : origin.X + 2,
                    origin.Y - _baseline,
                    _width - 4,
                    _height));
    }
}
