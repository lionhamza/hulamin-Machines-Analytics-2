using Microsoft.Web.WebView2.Core;
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Collections.Generic;

namespace Hulamin.Dashboard
{
    public partial class MainWindow : Window
    {
        private readonly HttpClient _client = new HttpClient();
        private bool _pageReady = false;

        public MainWindow()
        {
            InitializeComponent();
            InitBrowser();
            LoadMachines();
        }

        private async void InitBrowser()
        {
            await Browser.EnsureCoreWebView2Async();

            Browser.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;

            string path = System.IO.Path.Combine(
                Environment.CurrentDirectory, "powerbi.html");

            Browser.CoreWebView2.Navigate(new Uri(path).AbsoluteUri);
        }

        private void CoreWebView2_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _pageReady = true;
        }

        private async void LoadMachines()
        {
            var machines = await _client.GetFromJsonAsync<List<MachineDto>>(
                "http://localhost:5287/api/production/machines");

            MachineDropdown.ItemsSource = machines;
            MachineDropdown.SelectedValuePath = "machine_id";
            MachineDropdown.DisplayMemberPath = "machine_name";
        }

        private async void LoadReport_Click(object sender, RoutedEventArgs e)
        {
            if (!_pageReady)
            {
                MessageBox.Show("Browser not ready yet");
                return;
            }

            string machineId = MachineDropdown.SelectedValue.ToString();
            string start = StartDate.SelectedDate.Value.ToString("yyyy-MM-dd");
            string end = EndDate.SelectedDate.Value.ToString("yyyy-MM-dd");

            var token = await _client.GetFromJsonAsync<EmbedTokenDto>(
                "http://localhost:5287/api/powerbi/embed-token");

            await Browser.CoreWebView2.ExecuteScriptAsync(
                $"loadReport('{token.embedUrl}', '{token.accessToken}', '{token.reportId}')");

            await Task.Delay(2000);

            await Browser.CoreWebView2.ExecuteScriptAsync(
                $"applyFilters('{machineId}', '{start}', '{end}')");
        }
    }

    public class MachineDto
    {
        public string machine_id { get; set; }
        public string machine_name { get; set; }
    }

    public class EmbedTokenDto
    {
        public string accessToken { get; set; }
        public string embedUrl { get; set; }
        public string reportId { get; set; }
    }
}