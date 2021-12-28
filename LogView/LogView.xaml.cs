using System;
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
using Core;

namespace LogView
{
    /// <summary>
    /// Interaction logic for LogView.xaml
    /// </summary>
    public partial class LogView : UserControl
    {


        public LogView()
        {
            InitializeComponent();

            Fill();

            Logger.LoggerUpdated += Add;
        }

        private void Fill()
        {
            foreach (var tab in Logger.Log)
            {
                foreach (var record in tab.Value)
                {
                    Add(tab.Key, record);
                }
            }
        }

        private void Add(string header, object record)
        {
            TabItem tabItem = tabControl.Items.Cast<TabItem>().FirstOrDefault(n => (string)n.Header == header);
            //DataGrid dataGrid;

            if (tabItem == null)
            {
                DataGrid dataGrid = new DataGrid() { ItemsSource = Logger.Log[header] };

                tabItem = new TabItem() { Header = header, Content = dataGrid };
                tabControl.Items.Add(tabItem);
            }
            //else
            //{
            //    dataGrid = (DataGrid)tabItem.Content;
            //}

            //if(key == "system")
            //{
            //    //dataGrid.Items.Add(new { Value = newValue.ToString() });
            //}
            //else
            //{
            //    dataGrid.Items.Add(newValue);
            //}

        }
    }
}