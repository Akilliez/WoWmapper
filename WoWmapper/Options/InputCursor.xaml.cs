﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Xceed.Wpf.Toolkit;
using WoWmapper;
using WoWmapper.Controllers;

namespace WoWmapper.Options
{
    /// <summary>
    /// Interaction logic for InputCursor.xaml
    /// </summary>
    public partial class InputCursor : UserControl
    {
        public InputCursor()
        {
            InitializeComponent();
        }

        private void SaveSettings(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.Save();
        }
    }
}
