using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Shared.PromptTextBox;

public partial class PromptTextBoxViewModel : ObservableObject
{
    [ObservableProperty] private string _title;
    [ObservableProperty] private string _text;
    [ObservableProperty] private int _textBoxWidth;
    [ObservableProperty] private int _textBoxHeight;
    [ObservableProperty] private bool _isReadOnly;
    [ObservableProperty] private bool _isExtraButtonVisible;
    [ObservableProperty] private string _extraButtonText;

    private bool _returnSubmits;
    private Func<Task<string?>>? _extraButtonHandler;

    public Window? Window { get; set; }

    public bool OkPressed { get; private set; }

    public PromptTextBoxViewModel()
    {
        Title = string.Empty;
        Text = string.Empty;
        ExtraButtonText = string.Empty;
    }

    internal void Initialize(string title, string text, int textBoxWidth, int textBoxHeight, bool returnSubmits = false, bool isReadOnly = false)
    {
        Title = title;
        Text = text;
        TextBoxWidth = textBoxWidth;
        TextBoxHeight = textBoxHeight;
        IsReadOnly = isReadOnly;
        _returnSubmits = returnSubmits;
    }

    /// <summary>
    /// Adds an optional extra button next to OK/Cancel. The handler is invoked when the
    /// button is clicked; if it returns non-null text, the textbox content is replaced.
    /// </summary>
    internal void ConfigureExtraButton(string buttonText, Func<Task<string?>> handler)
    {
        ExtraButtonText = buttonText;
        _extraButtonHandler = handler;
        IsExtraButtonVisible = true;
    }

    [RelayCommand]
    private void Ok()
    {
        OkPressed = true;
        Window?.Close();
    }

    [RelayCommand]
    private void Cancel()
    {
        Window?.Close();
    }

    [RelayCommand]
    private async Task ExtraButton()
    {
        if (_extraButtonHandler == null)
        {
            return;
        }

        var result = await _extraButtonHandler();
        if (result != null)
        {
            Text = result;
        }
    }

    internal void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Window?.Close();
        }
        else if (e.Key == Key.Enter && _returnSubmits)
        {
            e.Handled = true;
            Ok();
        }
    }
}