using Avalonia.Controls;
using Nikse.SubtitleEdit.Logic;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Tools.BatchConvert.FunctionViews;

public static class ViewAssaChangeResolution
{
    public static Control Make(BatchConvertViewModel vm)
    {
        var labelHeader = new Label
        {
            Content = Se.Language.Assa.ResolutionResamplerTitle,
            FontWeight = Avalonia.Media.FontWeight.Bold,
            Margin = new Avalonia.Thickness(0, 0, 0, 10),
        };

        var labelOnlyAssa = new Label
        {
            Content = Se.Language.Tools.BatchConvert.AssaChangeResolutionOnlyAppliesToAssa,
            FontStyle = Avalonia.Media.FontStyle.Italic,
            Margin = new Avalonia.Thickness(0, 0, 0, 10),
        };

        var labelTargetWidth = UiUtil.MakeLabel(Se.Language.General.Width);
        var numericUpDownTargetWidth = UiUtil.MakeNumericUpDownInt(1, 10000, 1920, 130, vm, nameof(vm.AssaChangeResolutionTargetWidth));

        var labelTargetHeight = UiUtil.MakeLabel(Se.Language.General.Height);
        var numericUpDownTargetHeight = UiUtil.MakeNumericUpDownInt(1, 10000, 1080, 130, vm, nameof(vm.AssaChangeResolutionTargetHeight));

        var checkBoxMargins = UiUtil.MakeCheckBox(Se.Language.Assa.ResolutionResamplerChangeMargins, vm, nameof(vm.AssaChangeResolutionChangeMargins));
        var checkBoxFontSize = UiUtil.MakeCheckBox(Se.Language.Assa.ResolutionResamplerChangeFontSize, vm, nameof(vm.AssaChangeResolutionChangeFontSize));
        var checkBoxPosition = UiUtil.MakeCheckBox(Se.Language.Assa.ResolutionResamplerChangePositions, vm, nameof(vm.AssaChangeResolutionChangePosition));
        var checkBoxDrawing = UiUtil.MakeCheckBox(Se.Language.Assa.ResolutionResamplerChangeDrawing, vm, nameof(vm.AssaChangeResolutionChangeDrawing));

        var labelTargetResolution = new Label
        {
            Content = Se.Language.Assa.ResolutionResamplerTargetRes,
            Margin = new Avalonia.Thickness(0, 5, 0, 0),
        };

        var grid = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }, // Header
                new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }, // Only-ASSA note
                new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }, // Target res label
                new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }, // Width
                new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }, // Height
                new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }, // Margins
                new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }, // Font size
                new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }, // Position
                new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }, // Drawing
            },
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
            Margin = new Avalonia.Thickness(10),
            ColumnSpacing = 10,
            RowSpacing = 10,
        };

        grid.Add(labelHeader, 0, 0, 1, 2);
        grid.Add(labelOnlyAssa, 1, 0, 1, 2);
        grid.Add(labelTargetResolution, 2, 0, 1, 2);

        grid.Add(labelTargetWidth, 3);
        grid.Add(numericUpDownTargetWidth, 3, 1);

        grid.Add(labelTargetHeight, 4);
        grid.Add(numericUpDownTargetHeight, 4, 1);

        grid.Add(checkBoxMargins, 5, 0, 1, 2);
        grid.Add(checkBoxFontSize, 6, 0, 1, 2);
        grid.Add(checkBoxPosition, 7, 0, 1, 2);
        grid.Add(checkBoxDrawing, 8, 0, 1, 2);

        return grid;
    }
}
