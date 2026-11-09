using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;

namespace VibeModel.UI
{
    /// <summary>
    /// Plan 20: the mode chip in the chat input's footer (Cursor-style). Shows the change-approval
    /// mode in force — Automatisch / Vragen / Alleen lezen — and opens a menu to pick it for this
    /// Revit session (<see cref="ChangeApprovalPolicy.ChooseForSession"/>) and to send the next
    /// message as a preview. Locked (greyed, no menu) when managed.json sets the mode.
    /// The state lives in <see cref="ChangeApprovalPolicy"/>; <see cref="Refresh"/> re-reads it.
    /// </summary>
    internal sealed class ModeChip : Button
    {
        public static readonly SolidColorBrush Teal = Frozen(0x4E, 0xC9, 0xB0);
        public static readonly SolidColorBrush Amber = Frozen(0xE5, 0xA5, 0x3A);
        public static readonly SolidColorBrush Grey = Frozen(0x9A, 0x9A, 0xA5);
        private static readonly SolidColorBrush ChipBg = Frozen(0x3A, 0x3A, 0x40);
        private static readonly SolidColorBrush ChipBorder = Frozen(0x50, 0x50, 0x58);
        private static readonly SolidColorBrush MenuBg = Frozen(0x2D, 0x2D, 0x30);
        private static readonly SolidColorBrush MenuBorder = Frozen(0x55, 0x55, 0x5C);
        private static readonly SolidColorBrush RowHover = Frozen(0x3A, 0x3A, 0x40);

        // Segoe MDL2 Assets ships with Windows 10/11; without it a coloured dot stands in.
        private static readonly FontFamily IconFont = new FontFamily("Segoe MDL2 Assets");
        private static readonly bool HasIconFont =
            Fonts.SystemFontFamilies.Any(f => string.Equals(f.Source, "Segoe MDL2 Assets", StringComparison.OrdinalIgnoreCase));
        private const string GlyphAuto = "", GlyphAsk = "", GlyphReadOnly = "",
            GlyphCheck = "", GlyphChevron = "";

        private static ControlTemplate _pillTemplate;

        private readonly TextBlock _icon;
        private readonly TextBlock _name;
        private readonly TextBlock _badge;
        private readonly TextBlock _chevron;
        private Popup _menu;
        private bool _previewNext;

        /// <summary>The user picked a mode (already applied to the session).</summary>
        public event EventHandler ModeChanged;

        public ModeChip()
        {
            Template = PillTemplate();
            Background = ChipBg;
            BorderBrush = ChipBorder;
            BorderThickness = new Thickness(1);
            Padding = new Thickness(8, 2, 8, 2);
            Height = 22;
            Margin = new Thickness(2, 0, 0, 0);
            VerticalAlignment = VerticalAlignment.Center;
            Cursor = Cursors.Hand;
            AutomationProperties.SetAutomationId(this, "ModeChip");

            _icon = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
            _name = new TextBlock { FontSize = 11, Foreground = Palette.FgPrimary, VerticalAlignment = VerticalAlignment.Center };
            _badge = new TextBlock
            {
                FontSize = 10, Foreground = Palette.FgSecondary, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0), Visibility = Visibility.Collapsed
            };
            _chevron = new TextBlock
            {
                Text = HasIconFont ? GlyphChevron : "▾", FontFamily = HasIconFont ? IconFont : FontFamily,
                FontSize = HasIconFont ? 7 : 10, Foreground = Palette.FgSecondary,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 1, 0, 0)
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_icon);
            row.Children.Add(_name);
            row.Children.Add(_badge);
            row.Children.Add(_chevron);
            Content = row;

            Click += (s, e) => OpenMenu();
            MouseEnter += (s, e) => { if (IsLocked == false) BorderBrush = Teal; };
            MouseLeave += (s, e) => BorderBrush = ChipBorder;
            Refresh();
        }

        /// <summary>The mode the chip shows: the one in force (Vragen with "don't ask again" ticked shows Automatisch).</summary>
        public ChangeApprovalMode Shown { get; private set; }

        /// <summary>managed.json sets the mode: no menu.</summary>
        public bool IsLocked { get; private set; }

        /// <summary>"Next message as preview" is on; the pane takes it (and clears it) when it sends.</summary>
        public bool PreviewNext
        {
            get => _previewNext;
            set { _previewNext = value; Refresh(); }
        }

        /// <summary>Re-reads the policy (Settings, the approval window's checkbox, managed.json may have changed it).</summary>
        public void Refresh()
        {
            var language = Strings.Current;
            var chosen = ChangeApprovalPolicy.Chosen(out var source);
            bool dontAsk = ChangeApprovalPolicy.DontAskThisSession;
            Shown = ChangeApprovalPolicy.Effective(chosen, dontAsk);
            IsLocked = source == ChangeApprovalSource.Managed;

            SetIcon(_icon, Shown, IsLocked ? Palette.FgSecondary : ColourOf(Shown));
            _name.Text = UiText.ModeName(Shown, language);
            _name.Foreground = IsLocked ? Palette.FgSecondary : Palette.FgPrimary;
            _badge.Text = Strings.For(language, Strings.ModePreviewBadge);
            _badge.Visibility = _previewNext ? Visibility.Visible : Visibility.Collapsed;
            _chevron.Visibility = IsLocked ? Visibility.Collapsed : Visibility.Visible;
            Cursor = IsLocked ? Cursors.Arrow : Cursors.Hand;
            ToolTip = UiText.ModeChipTip(chosen, source, dontAsk, language);
            AutomationProperties.SetName(this, _name.Text + (_previewNext ? " " + _badge.Text : ""));
        }

        public static SolidColorBrush ColourOf(ChangeApprovalMode mode) =>
            mode == ChangeApprovalMode.Ask ? Amber : mode == ChangeApprovalMode.ReadOnly ? Grey : Teal;

        public void CloseMenu()
        {
            if (_menu != null) _menu.IsOpen = false;
        }

        private void OpenMenu()
        {
            Refresh();
            if (IsLocked) return;
            CloseMenu();

            var language = Strings.Current;
            var list = new StackPanel { Margin = new Thickness(4) };
            foreach (var mode in new[] { ChangeApprovalMode.Auto, ChangeApprovalMode.Ask, ChangeApprovalMode.ReadOnly })
                list.Children.Add(ModeRow(mode, language));

            list.Children.Add(new Border { Height = 1, Background = MenuBorder, Margin = new Thickness(4, 4, 4, 4) });

            var previewText = new StackPanel();
            previewText.Children.Add(new TextBlock
            {
                Text = Strings.For(language, Strings.ModePreviewNext), Foreground = Palette.FgPrimary, FontSize = 12
            });
            previewText.Children.Add(new TextBlock
            {
                Text = Strings.For(language, Strings.ModePreviewNextHint), Foreground = Palette.FgSecondary, FontSize = 10
            });
            var toggle = new CheckBox
            {
                Content = previewText,
                IsChecked = _previewNext,
                Foreground = Palette.FgPrimary,
                Margin = new Thickness(8, 2, 8, 6),
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand
            };
            AutomationProperties.SetAutomationId(toggle, "PreviewNextToggle");
            AutomationProperties.SetName(toggle, Strings.For(language, Strings.ModePreviewNext));
            // Checked/Unchecked, not Click: UI Automation's Toggle does not raise Click.
            toggle.Checked += (s, e) => PreviewNext = true;
            toggle.Unchecked += (s, e) => PreviewNext = false;
            list.Children.Add(toggle);

            var border = new Border
            {
                Background = MenuBg,
                BorderBrush = MenuBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                MinWidth = 250,
                Focusable = true,
                Child = list
            };
            border.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { CloseMenu(); Focus(); e.Handled = true; }
            };

            var menu = new Popup
            {
                Child = border,
                PlacementTarget = this,
                Placement = PlacementMode.Top,
                VerticalOffset = -4,
                StaysOpen = false,
                AllowsTransparency = true
            };
            menu.Opened += (s, e) => border.Focus();
            menu.Closed += (s, e) => { if (_menu == menu) _menu = null; };
            _menu = menu;
            menu.IsOpen = true;
        }

        // One menu row: icon, bold name, one-line description, a check on the mode in force.
        private Button ModeRow(ChangeApprovalMode mode, string language)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });

            var icon = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            SetIcon(icon, mode, ColourOf(mode));
            grid.Children.Add(icon);

            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = UiText.ModeName(mode, language), FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = Palette.FgPrimary
            });
            text.Children.Add(new TextBlock
            {
                Text = UiText.ModeHint(mode, language), FontSize = 10, Foreground = Palette.FgSecondary
            });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            if (mode == Shown)
            {
                var check = new TextBlock
                {
                    Text = HasIconFont ? GlyphCheck : "✓", FontFamily = HasIconFont ? IconFont : FontFamily,
                    FontSize = 11, Foreground = Teal, VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(check, 2);
                grid.Children.Add(check);
            }

            var row = new Button
            {
                Template = PillTemplate(),
                Content = grid,
                Background = mode == Shown ? RowHover : Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 5, 8, 5),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand
            };
            AutomationProperties.SetAutomationId(row, mode == ChangeApprovalMode.Ask ? "ModeAsk"
                : mode == ChangeApprovalMode.ReadOnly ? "ModeReadOnly" : "ModeAuto");
            AutomationProperties.SetName(row, UiText.ModeName(mode, language));
            row.MouseEnter += (s, e) => row.Background = RowHover;
            row.MouseLeave += (s, e) => row.Background = mode == Shown ? RowHover : Brushes.Transparent;
            row.Click += (s, e) =>
            {
                ChangeApprovalPolicy.ChooseForSession(mode);
                CloseMenu();
                Refresh();
                ModeChanged?.Invoke(this, EventArgs.Empty);
            };
            return row;
        }

        private void SetIcon(TextBlock target, ChangeApprovalMode mode, Brush colour)
        {
            target.Foreground = colour;
            if (HasIconFont)
            {
                target.FontFamily = IconFont;
                target.Text = mode == ChangeApprovalMode.Ask ? GlyphAsk : mode == ChangeApprovalMode.ReadOnly ? GlyphReadOnly : GlyphAuto;
            }
            else
            {
                target.FontFamily = FontFamily;
                target.Text = "●";
            }
        }

        // A rounded pill (also the menu rows): the background/border bindings the chip changes.
        private static ControlTemplate PillTemplate()
        {
            if (_pillTemplate != null) return _pillTemplate;
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(HorizontalContentAlignmentProperty));
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            _pillTemplate = new ControlTemplate(typeof(Button)) { VisualTree = border };
            return _pillTemplate;
        }

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
