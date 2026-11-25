using System;
using System.Windows.Controls;

namespace BBTech.LaptopTool.UI.Views
{
    public partial class EcommerceDashboardView : UserControl
    {
        private readonly MainWindow _shell;

        public EcommerceDashboardView(MainWindow shell)
        {
            InitializeComponent();
            _shell = shell ?? throw new ArgumentNullException(nameof(shell));
            Loaded += (s, e) =>
            {
                DetectViewInstance.StartDetection();
            };
        }

        public DetectView Detect => DetectViewInstance;
    }
}
