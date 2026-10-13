using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Girt.Controls
{
    /// <summary>A TextBox with an inline ✕ button (PART_ClearButton, shown only while there's
    /// text) that empties it in one click. Styled by ClearableTextBoxStyle in Styles.xaml.</summary>
    public class ClearableTextBox : TextBox
    {
        private ButtonBase? _clearButton;

        public override void OnApplyTemplate()
        {
            if (_clearButton != null) _clearButton.Click -= OnClearClicked;
            base.OnApplyTemplate();
            _clearButton = GetTemplateChild("PART_ClearButton") as ButtonBase;
            if (_clearButton != null) _clearButton.Click += OnClearClicked;
        }

        private void OnClearClicked(object sender, RoutedEventArgs e)
        {
            Clear();
            Focus();
        }
    }
}
