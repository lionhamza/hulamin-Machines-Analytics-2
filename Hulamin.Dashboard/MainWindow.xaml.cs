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

        public MainWindow()
        {
            InitializeComponent();
            InitBrowser();
            LoadMachines();
        }

        private async void InitBrowser()
        {
            await Browser.EnsureCoreWebView2Async();
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
            try
            {
                if (MachineDropdown.SelectedValue == null ||
                    StartDate.SelectedDate == null ||
                    EndDate.SelectedDate == null)
                {
                    MessageBox.Show("Select machine and dates");
                    return;
                }

                string machineId = MachineDropdown.SelectedValue.ToString();

                string startFormatted = StartDate.SelectedDate.Value.ToString("yyyy-MM-ddT00:00:00");
                string endFormatted   = EndDate.SelectedDate.Value.ToString("yyyy-MM-ddT23:59:59");

                // OPTIONAL API call (your backend)
                string apiUrl =
                    $"http://localhost:5287/api/production/waterfall?machineId={machineId}&startDate={StartDate.SelectedDate.Value:yyyy-MM-dd}&endDate={EndDate.SelectedDate.Value:yyyy-MM-dd}";

                await _client.GetStringAsync(apiUrl);

                // ✅ FIXED Power BI filter (NO demo mode, correct encoding)
                string filter =
                    $"public production/machine_id eq '{machineId}' " +
                    $"and public production/date ge {startFormatted} " +
                    $"and public production/date le {endFormatted}";

                string encodedFilter = Uri.EscapeDataString(filter);

                string powerBiUrl =
                    $"https://app.powerbi.com/reportEmbed?reportId=ddcfcb63-79e7-47d2-9a9f-30d6302519b9" +
                    $"&autoAuth=true" +
                    $"&filter={encodedFilter}";

                Browser.CoreWebView2.Navigate(powerBiUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }
    }

    public class MachineDto
    {
        public string machine_id { get; set; }
        public string machine_name { get; set; }
    }
}