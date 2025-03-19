using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

namespace clientPrintQueueMonitorService
{
    public partial class ClientForm : Form
    {
        private string _serverAddress = "127.0.0.1";
        private int _serverPort = 8005;
        private TcpClient _client;
        private StreamReader _reader;
        private StreamWriter _writer;
        private bool _isConnected = false;

        private System.Timers.Timer _dataFetchTimer; // Таймер для запроса данных
        private int _fetchInterval = 5000; // Интервал запроса данных (в миллисекундах)

        private string[] printers;

        public ClientForm()
        {
            InitializeComponent();
            InitializeChart();
        }

        private void InitializeChart()
        {
            // Настройка графика
            chart1.ChartAreas.Clear();
            chart1.Series.Clear();

            ChartArea chartArea = new ChartArea("MainArea");
            chart1.ChartAreas.Add(chartArea);

            Series series = new Series("QueueLength");
            series.ChartType = SeriesChartType.Line; // Линейный график
            series.XValueType = ChartValueType.DateTime; // Ось X - время
            chart1.Series.Add(series);

            chart1.ChartAreas["MainArea"].AxisX.Title = "Время";
            chart1.ChartAreas["MainArea"].AxisY.Title = "Длина очереди";
        }

        //Кнопка подключения к серверу
        private async void connectButton_Click(object sender, EventArgs e)
        {
            try
            {
                _client = new TcpClient();
                await _client.ConnectAsync(_serverAddress, _serverPort);

                NetworkStream stream = _client.GetStream();
                _reader = new StreamReader(stream, Encoding.UTF8);
                _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                _isConnected = true;
                connectButton.Enabled = false;
                disconnectButton.Enabled = true;
                //sendCommandButton.Enabled = true;
                //getPrintersButton.Enabled = true;
                //.Enabled = true;

                LogMessage("Подключено к серверу.");

                _dataFetchTimer = new System.Timers.Timer(_fetchInterval);
                _dataFetchTimer.Elapsed += OnDataFetchTimerElapsed;
                _dataFetchTimer.AutoReset = true;
                _dataFetchTimer.Enabled = true;

                await Task.Run(() => { SendCommandPrinters(); });
            }
            catch (Exception ex)
            {
                LogMessage($"Ошибка подключения: {ex.Message}");
                _isConnected = false;
            }
        }

        private void OnDataFetchTimerElapsed(object sender, ElapsedEventArgs e)
        {
            FetchDataFromServer();
        }

        private async void FetchDataFromServer()
        {
            await SendCommand("GetQueueLength");
        }

        private async void disconnectButton_Click(object sender, EventArgs e)
        {
            Disconnect();
        }

        private void Disconnect()
        {
            if (_isConnected)
            {
                _isConnected = false;
                _client?.Close();
                _reader?.Close();
                _writer?.Close();

                connectButton.Enabled = true;
                disconnectButton.Enabled = false;
                //sendCommandButton.Enabled = false;
                //getPrintersButton.Enabled = false;
                //setSettingsButton.Enabled = false;
                _dataFetchTimer.Enabled = false;
                LogMessage("Отключено от сервера.");
            }
        }

        private async void sendCommandButton_Click(object sender, EventArgs e)
        {
            string command = "SET_SETTINGS";
            Settings settings = new Settings();
            settings.Printers = printersListBox.SelectedItems.Cast<string>().ToList();
            settings.Interval = intervalNumericUpDown.Value;

            string jsonSettings = JsonConvert.SerializeObject(settings); // Сериализация в JSON
            command += ";" + jsonSettings;

            await SendCommand(command);
        }

        private async Task SendCommand(string command)
        {
            if (_isConnected)
            {
                try
                {
                    await _writer.WriteLineAsync(command);
                    string response = await _reader.ReadLineAsync();
                    HandleResponse(command, response); //  Обработка ответа
                }
                catch (Exception ex)
                {
                    LogMessage($"Ошибка отправки/получения данных: {ex.Message}");
                    Disconnect(); // При ошибке отключаемся
                }
            }
            else
            {
                LogMessage("Не подключено к серверу.");
            }
        }

        private void HandleResponse(string command, string response)
        {
            if (command == "GetQueueLength" && response != null && response.StartsWith("QUEUE_LENGTH:"))
            {
                try
                {
                    int queueLength = int.Parse(response.Substring("QUEUE_LENGTH:".Length));
                    UpdateChart(queueLength);
                }
                catch (Exception ex)
                {
                    LogMessage($"Ошибка обработки длины очереди: {ex.Message}");
                }
            }
            else if (command == "GET_PRINTERS")
            {
                try
                {
                    //string printersJson = response.Substring("OK:".Length);
                    string[] printers = response.Split(';');

                    printersListBox.Invoke(new Action(() =>
                    {
                        DisplayPrinters(printers);
                    }));
                }
                catch (Exception ex)
                {
                    LogMessage($"Ошибка обработки списка принтеров: {ex.Message}");
                }
            }
            else if (response != null && response.StartsWith("OK:") && command.StartsWith("SET_SETTINGS"))
            {
                LogMessage("Настройки успешно применены на сервере.");
            }
            else if (response != null && response.StartsWith("ERROR:"))
            {
                LogMessage($"Ошибка от сервера: {response}");
            }
        }

        private void LogMessage(string message)
        {
            if (logTextBox.InvokeRequired)
            {
                logTextBox.Invoke(new Action<string>(LogMessage), message);
            }
            else
            {
                logTextBox.AppendText(message + Environment.NewLine);
            }
        }

        private async void SendCommandPrinters()
        {
            await SendCommand("GET_PRINTERS");
        }

        private void DisplayPrinters(string[] printers)
        {
            int i = 0;
            printersListBox.Items.Clear();
            foreach (string printer in printers)
            {
                i++;
                printersListBox.Items.Add(printer);
            }

            LogMessage($"Добавлено {i} принтеров");
        }

        private void UpdateChart(int queueLength)
        {
            // Потокобезопасное обновление графика
            if (chart1.InvokeRequired)
            {
                chart1.Invoke(new Action<int>(UpdateChart), queueLength);
            }
            else
            {
                chart1.Series["QueueLength"].Points.AddXY(DateTime.Now, queueLength);

                // Ограничение количества точек на графике (например, 100)
                if (chart1.Series["QueueLength"].Points.Count > 100)
                {
                    chart1.Series["QueueLength"].Points.RemoveAt(0); // Удаляем самую старую точку
                }

                // Автоматическое масштабирование осей
                chart1.ChartAreas["MainArea"].AxisX.ScaleView.ZoomReset();
                chart1.ChartAreas["MainArea"].AxisY.ScaleView.ZoomReset();
            }
        }
    }
}

public class Settings
{
    public List<string> Printers { get; set; }
    public decimal Interval { get; set; }
}