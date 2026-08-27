using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SnapX.Core;
using SnapX.Core.Hotkey;
using SnapX.Avalonia.ViewModels.Settings;

namespace SnapX.Avalonia.Views.Settings.Views;

public partial class SettingsCategoryView : UserControl
{
    public SettingsCategoryView()
    {
        InitializeComponent();
    }

    private async void ApplyHotkeyButton_OnClick(object? sender, RoutedEventArgs e)
    {
        DebugHelper.WriteLine(
            $"ApplyHotkeyButton_OnClick: DataContext is SettingsCategoryVM: {DataContext is SettingsCategoryVM}, sender is Button: {sender is Button}, sender.DataContext is HotkeyEditorRow: {(sender as Button)?.DataContext is SettingsCategoryVM.HotkeyEditorRow}.");
        if (DataContext is SettingsCategoryVM vm && sender is Button { DataContext: SettingsCategoryVM.HotkeyEditorRow row })
            await vm.ApplyHotkeyAsync(row);
    }

    private async void ClearHotkeyButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsCategoryVM vm && sender is Button { DataContext: SettingsCategoryVM.HotkeyEditorRow row })
            await vm.ClearHotkeyAsync(row);
    }

    private void HotkeyTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: SettingsCategoryVM.HotkeyEditorRow row }) return;

        if (TryCreateHotkey(e.Key, e.KeyModifiers, out Keys hotkey, out bool win))
        {
            row.SetCapturedShortcut(hotkey, win);
        }
        else if (!IsModifierKey(e.Key))
        {
            row.SetError("SnapX cannot use this key as a global shortcut.");
        }

        // Do not put control characters or a partial shortcut in the text box.
        e.Handled = true;
    }

    private static bool TryCreateHotkey(Key key, KeyModifiers modifiers, out Keys hotkey, out bool win)
    {
        Keys keyCode = key switch
        {
            Key.A => Keys.A, Key.B => Keys.B, Key.C => Keys.C, Key.D => Keys.D, Key.E => Keys.E,
            Key.F => Keys.F, Key.G => Keys.G, Key.H => Keys.H, Key.I => Keys.I, Key.J => Keys.J,
            Key.K => Keys.K, Key.L => Keys.L, Key.M => Keys.M, Key.N => Keys.N, Key.O => Keys.O,
            Key.P => Keys.P, Key.Q => Keys.Q, Key.R => Keys.R, Key.S => Keys.S, Key.T => Keys.T,
            Key.U => Keys.U, Key.V => Keys.V, Key.W => Keys.W, Key.X => Keys.X, Key.Y => Keys.Y,
            Key.Z => Keys.Z,
            Key.D0 => Keys.D0, Key.D1 => Keys.D1, Key.D2 => Keys.D2, Key.D3 => Keys.D3, Key.D4 => Keys.D4,
            Key.D5 => Keys.D5, Key.D6 => Keys.D6, Key.D7 => Keys.D7, Key.D8 => Keys.D8, Key.D9 => Keys.D9,
            Key.NumPad0 => Keys.NumPad0, Key.NumPad1 => Keys.NumPad1, Key.NumPad2 => Keys.NumPad2,
            Key.NumPad3 => Keys.NumPad3, Key.NumPad4 => Keys.NumPad4, Key.NumPad5 => Keys.NumPad5,
            Key.NumPad6 => Keys.NumPad6, Key.NumPad7 => Keys.NumPad7, Key.NumPad8 => Keys.NumPad8,
            Key.NumPad9 => Keys.NumPad9,
            Key.F1 => Keys.F1, Key.F2 => Keys.F2, Key.F3 => Keys.F3, Key.F4 => Keys.F4, Key.F5 => Keys.F5,
            Key.F6 => Keys.F6, Key.F7 => Keys.F7, Key.F8 => Keys.F8, Key.F9 => Keys.F9, Key.F10 => Keys.F10,
            Key.F11 => Keys.F11, Key.F12 => Keys.F12, Key.F13 => Keys.F13, Key.F14 => Keys.F14,
            Key.F15 => Keys.F15, Key.F16 => Keys.F16, Key.F17 => Keys.F17, Key.F18 => Keys.F18,
            Key.F19 => Keys.F19, Key.F20 => Keys.F20, Key.F21 => Keys.F21, Key.F22 => Keys.F22,
            Key.F23 => Keys.F23, Key.F24 => Keys.F24,
            Key.Enter => Keys.Return, Key.Space => Keys.Space, Key.Tab => Keys.Tab, Key.Escape => Keys.Escape,
            Key.Delete => Keys.Delete, Key.Insert => Keys.Insert, Key.Home => Keys.Home, Key.End => Keys.End,
            Key.PageUp => Keys.PageUp, Key.PageDown => Keys.PageDown, Key.Left => Keys.Left, Key.Up => Keys.Up,
            Key.Right => Keys.Right, Key.Down => Keys.Down, Key.PrintScreen => Keys.PrintScreen,
            Key.Add => Keys.NumPadAdd, Key.Subtract => Keys.NumPadSubtract, Key.Multiply => Keys.NumPadMultiply,
            Key.Divide => Keys.NumPadDivide, Key.Decimal => Keys.NumPadDecimal,
            _ => Keys.None
        };

        win = modifiers.HasFlag(KeyModifiers.Meta);
        hotkey = keyCode;
        if (modifiers.HasFlag(KeyModifiers.Control)) hotkey |= Keys.Control;
        if (modifiers.HasFlag(KeyModifiers.Shift)) hotkey |= Keys.Shift;
        if (modifiers.HasFlag(KeyModifiers.Alt)) hotkey |= Keys.Alt;

        return keyCode != Keys.None && new HotkeyInfo(hotkey) { Win = win }.IsValidHotkey;
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
}
