using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;

namespace PatientIntakeApp.ViewModels;

public enum DialogButtons
{
    Ok,
    YesNo
}

public enum DialogResult
{
    None = 0,
    Ok = 1,
    Yes = 2,
    No = 3
}

public partial class DialogViewModel : ObservableObject
{
    private readonly Action<DialogResult> _close;

    public DialogViewModel(
        string title,
        string message,
        PackIconKind iconKind,
        DialogButtons buttons,
        Action<DialogResult> close,
        string? okText = null,
        string? yesText = null,
        string? noText = null)
    {
        Title = title;
        Message = message;
        IconKind = iconKind;
        Buttons = buttons;
        _close = close;

        OkText = string.IsNullOrWhiteSpace(okText) ? "OK" : okText;
        YesText = string.IsNullOrWhiteSpace(yesText) ? "YES" : yesText;
        NoText = string.IsNullOrWhiteSpace(noText) ? "NO" : noText;
    }

    public string Title { get; }
    public string Message { get; }
    public PackIconKind IconKind { get; }
    public DialogButtons Buttons { get; }
    public string OkText { get; }
    public string YesText { get; }
    public string NoText { get; }

    [RelayCommand]
    private void Ok() => _close(DialogResult.Ok);

    [RelayCommand]
    private void Yes() => _close(DialogResult.Yes);

    [RelayCommand]
    private void No() => _close(DialogResult.No);
}


