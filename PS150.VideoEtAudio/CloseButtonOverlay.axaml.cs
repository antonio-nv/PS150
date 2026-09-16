using System;
using Avalonia.Controls;

namespace PS150.VideoEtAudio
{
    public partial class CloseButtonOverlay : Window
    {
        public CloseButtonOverlay()
        {
            InitializeComponent();
        }

        public void SetCloseAction(Action onClose)
        {
            var button = this.FindControl<Button>("CloseButton");
            if (button != null)
            {
                button.Click += (s, e) => onClose();
            }
        }
    }
}
