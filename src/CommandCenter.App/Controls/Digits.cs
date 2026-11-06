using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CommandCenter.Controls
{
    // A text box that takes whole numbers only, typed or pasted: <TextBox c:Digits.Only="True"/>. Digits 0 to 9 only,
    // because those are the ones the settings files can read back.
    public static class Digits
    {
        public static readonly DependencyProperty OnlyProperty = DependencyProperty.RegisterAttached(
            "Only", typeof(bool), typeof(Digits), new PropertyMetadata(false, OnChanged));

        public static bool GetOnly(DependencyObject element) => (bool)element.GetValue(OnlyProperty);

        public static void SetOnly(DependencyObject element, bool value) => element.SetValue(OnlyProperty, value);

        private static void OnChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            if (element is not TextBox box)
                return;
            box.PreviewTextInput -= OnText;
            box.PreviewKeyDown -= OnKey;
            DataObject.RemovePastingHandler(box, OnPaste);
            if ((bool)e.NewValue)
            {
                box.PreviewTextInput += OnText;
                box.PreviewKeyDown += OnKey;
                DataObject.AddPastingHandler(box, OnPaste);
            }
        }

        private static bool AllDigits(string text) => text.Length > 0 && text.All(char.IsAsciiDigit);

        private static void OnText(object sender, TextCompositionEventArgs e) => e.Handled = !AllDigits(e.Text);

        // A text box takes the space bar without a text input event
        private static void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
                e.Handled = true;
        }

        private static void OnPaste(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !AllDigits(text))
                e.CancelCommand();
        }
    }
}
