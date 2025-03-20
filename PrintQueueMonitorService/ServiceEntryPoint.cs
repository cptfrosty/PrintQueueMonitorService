using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Sockets;
using System.Net;
using System.Printing;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Timers;

namespace PrintQueueMonitorService
{
    public partial class ServiceEntryPoint : ServiceBase
    {
        private System.Timers.Timer _timer;
        private string _reportFilePath;
        private List<string> _printerNames = new List<string>();
        private decimal _intervalInSeconds = 60;
        private Thread _tcpListenerThread;
        private TcpListener _listener;
        private int _tcpPort = 13000; // Порт для TCP-соединений
        private int lastAllQueueLength = 0;

        public ServiceEntryPoint()
        {
            InitializeComponent();
            this.ServiceName = "PrintQueueMonitorService";
            this.CanStop = true;
            this.CanPauseAndContinue = true;
            this.AutoLog = true;

            CreateDirectory();
        }

        protected override void OnStart(string[] args)
        {
            WriteToLog("Служба запущена.");

            // Запуск потока для прослушивания TCP-соединений
            _tcpListenerThread = new Thread(ListenForTcpConnections);
            _tcpListenerThread.Start();

            // Инициализация таймера
            _timer = new System.Timers.Timer(double.Parse(_intervalInSeconds.ToString()) * 1000);
            _timer.Elapsed += OnTimerElapsed;
            _timer.AutoReset = true;
            _timer.Enabled = true;
        }

        protected override void OnStop()
        {
            WriteToLog("Служба остановлена.");
            _timer.Stop();
            _timer.Dispose();

            // Остановка TCP-Listener
            if (_listener != null)
            {
                _listener.Stop();
            }
            // Останавливаем поток TCP
            if (_tcpListenerThread != null && _tcpListenerThread.IsAlive)
            {
                _tcpListenerThread.Abort(); // Не рекомендуется, но для примера.
                _tcpListenerThread.Join();
            }
        }

        protected override void OnPause()
        {
            WriteToLog("Служба приостановлена.");
            _timer.Stop();
        }

        protected override void OnContinue()
        {
            WriteToLog("Служба возобновлена.");
            _timer.Start();
        }

        private void OnTimerElapsed(object sender, ElapsedEventArgs e)
        {
            WriteToLog("Выполняется мониторинг очереди печати...");
            MonitorPrintQueues();
        }

        private void CreateDirectory()
        {
            // Получаем путь к папке ProgramData для хранения логов
            string logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MyPrintQueueService");

            // Создаем папку, если она не существует
            if (!Directory.Exists(logDirectory))
            {
                try
                {
                    Directory.CreateDirectory(logDirectory);
                }
                catch (Exception ex)
                {
                    WriteToLog($"Ошибка при создании папки логов: {ex.Message} - {ex.StackTrace}");
                    // В этом случае не удастся создать файл логов, но служба продолжит работу
                    return;
                }
            }

            // Формируем абсолютный путь к файлу логов
            _reportFilePath = Path.Combine(logDirectory, "PrintQueueReport.txt");
        }

        private void MonitorPrintQueues()
        {
            try
            {
                using (var server = new LocalPrintServer())
                {
                    int allQueueLength = 0; //Общая длина очереди 
                    foreach (string printerName in _printerNames)
                    {
                        try
                        {
                            //  PrintQueue printer = server.GetPrintQueue(printerName); // Удалено
                            int queueLength = GetPrintQueueLength(printerName);
                            allQueueLength += queueLength;
                            string reportLine = $"{DateTime.Now}: Принтер '{printerName}', длина очереди: {queueLength}";
                            WriteToReportFile(reportLine);
                            WriteToLog(reportLine);
                        }
                        catch (Exception ex)
                        {
                            WriteToLog($"Ошибка при мониторинге принтера '{printerName}': {ex.Message}");
                        }
                    }
                    lastAllQueueLength = allQueueLength;
                }
            }
            catch (Exception ex)
            {
                WriteToLog($"Общая ошибка при мониторинге: {ex.Message}");
            }
        }

        private int GetPrintQueueLength(string printerName)
        {
            int jobCount = 0;
            try
            {
                string query = string.Format("SELECT * FROM Win32_PrintJob WHERE Name LIKE '%{0}%'", printerName);
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(query))
                using (ManagementObjectCollection jobs = searcher.Get())
                {
                    jobCount = jobs.Count;
                }
            }
            catch (Exception ex)
            {
                WriteToLog($"Ошибка при получении длины очереди через WMI для принтера '{printerName}': {ex.Message}");
                return 0;
            }
            return jobCount;
        }

        private void WriteToReportFile(string message)
        {
            try
            {
                using (StreamWriter writer = new StreamWriter(_reportFilePath, true))
                {
                    writer.WriteLine(message);
                }
            }
            catch (Exception ex)
            {
                WriteToLog($"Ошибка при записи в файл отчета: {ex.Message}");
            }
        }

        private void WriteToLog(string message)
        {
            EventLog.WriteEntry(this.ServiceName, message, EventLogEntryType.Information);
        }

        // Методы для динамического изменения параметров (будут вызываться сервером)
        public void SetPrinterNames(List<string> printerNames)
        {
            _printerNames = printerNames;
            WriteToLog($"Список принтеров для мониторинга обновлен: {string.Join(", ", _printerNames)}");
        }

        public void SetInterval(decimal intervalInSeconds)
        {
            if (intervalInSeconds > 0)
            {
                _intervalInSeconds = intervalInSeconds;
                _timer.Interval = double.Parse(_intervalInSeconds.ToString()) * 1000;
                WriteToLog($"Интервал мониторинга обновлен: {_intervalInSeconds} секунд.");
            }
            else
            {
                WriteToLog("Недопустимый интервал. Интервал не был изменен.");
            }
        }

        // Метод для прослушивания TCP-соединений
        private void ListenForTcpConnections()
        {
            try
            {
                IPAddress ipAddress = IPAddress.Loopback; // Слушаем только локальные соединения
                _listener = new TcpListener(ipAddress, _tcpPort);
                _listener.Start();

                WriteToLog($"Служба прослушивает TCP-соединения на порту {_tcpPort}");

                while (true)
                {
                    try
                    {
                        TcpClient client = _listener.AcceptTcpClient();
                        WriteToLog("Принято входящее TCP-соединение.");

                        // Обработка клиента в отдельном потоке
                        Thread clientThread = new Thread(HandleTcpClient);
                        clientThread.Start(client);
                    }
                    catch (SocketException ex)
                    {
                        WriteToLog($"Ошибка SocketException при принятии TCP-соединения: {ex.Message}");
                        //  Обработка ошибок, связанных с сокетами (например, порт занят).
                    }
                    catch (Exception ex)
                    {
                        WriteToLog($"Непредвиденная ошибка при принятии TCP-соединения: {ex.Message}");
                    }
                }
            }
            catch (SocketException ex)
            {
                WriteToLog($"Ошибка при создании TCP-Listener: {ex.Message}");
                // Обработка ошибок, связанных с сокетами (например, порт занят).
            }
            catch (ThreadAbortException)
            {
                WriteToLog("Поток TCP Listener остановлен.");
            }
            catch (Exception ex)
            {
                WriteToLog($"Непредвиденная ошибка в потоке TCP Listener: {ex.Message}");
            }
        }

        // Метод для обработки TCP-клиента
        private void HandleTcpClient(object obj)
        {
            TcpClient client = (TcpClient)obj;

            try
            {
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                using (StreamWriter writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
                {
                    string command;
                    while ((command = reader.ReadLine()) != null && client.Connected)
                    {
                        WriteToLog($"Получена команда: {command}");

                        if (command.StartsWith("SetPrinters:"))
                        {
                            try
                            {
                                // Десериализуем только список принтеров (как и сериализуем на сервере)
                                string printersString = command.Substring("SetPrinters:".Length);
                                List<string> printers = JsonConvert.DeserializeObject<List<string>>(printersString);

                                SetPrinterNames(printers);
                                writer.WriteLine("OK:PrintersSet");
                                WriteToLog($"Отправлен ответ: OK:PrintersSet");
                            }
                            catch (JsonSerializationException ex)
                            {
                                WriteToLog($"Ошибка при десериализации JSON (SetPrinters): {ex.Message}");
                                writer.WriteLine($"ERROR:PrintersNotSet - Ошибка десериализации JSON: {ex.Message}");
                                WriteToLog($"Отправлен ответ: ERROR:PrintersNotSet - Ошибка десериализации JSON: {ex.Message}");
                            }
                            catch (Exception ex)
                            {
                                WriteToLog($"Ошибка при обработке команды SetPrinters: {ex.Message}");
                                writer.WriteLine($"ERROR:PrintersNotSet - {ex.Message}");
                                WriteToLog($"Отправлен ответ: ERROR:PrintersNotSet - {ex.Message}");
                            }
                        }
                        else if (command.StartsWith("SetInterval:")) // Обрабатываем SetInterval
                        {
                            try
                            {
                                string intervalString = command.Substring("SetInterval:".Length);
                                decimal intervalDecimal = decimal.Parse(intervalString);
                                int interval = (int)intervalDecimal;

                                SetInterval(interval);
                                writer.WriteLine("OK:IntervalSet");
                                WriteToLog($"Отправлен ответ: OK:IntervalSet");
                            }
                            catch (FormatException ex)
                            {
                                WriteToLog($"Ошибка при преобразовании интервала (SetInterval): {ex.Message}");
                                writer.WriteLine($"ERROR:IntervalNotSet - Неверный формат интервала: {ex.Message}");
                                WriteToLog($"Отправлен ответ: ERROR:IntervalNotSet - Неверный формат интервала: {ex.Message}");
                            }
                            catch (Exception ex)
                            {
                                WriteToLog($"Ошибка при обработке команды SetInterval: {ex.Message}");
                                writer.WriteLine($"ERROR:IntervalNotSet - {ex.Message}");
                                WriteToLog($"Отправлен ответ: ERROR:IntervalNotSet - {ex.Message}");
                            }
                        }
                        else if (command.StartsWith("GetCountQueue:")) //Получить кол-во очереди в печати
                        {
                            try
                            {
                                writer.WriteLine($"QUEUE_LENGTH:{lastAllQueueLength}");
                            }
                            catch (JsonSerializationException ex)
                            {
                                WriteToLog($"Ошибка при десериализации JSON (SetPrinters): {ex.Message}");
                                writer.WriteLine($"ERROR:PrintersNotSet - Ошибка десериализации JSON: {ex.Message}");
                                WriteToLog($"Отправлен ответ: ERROR:PrintersNotSet - Ошибка десериализации JSON: {ex.Message}");
                            }
                            catch (Exception ex)
                            {
                                WriteToLog($"Ошибка при обработке команды SetPrinters: {ex.Message}");
                                writer.WriteLine($"ERROR:PrintersNotSet - {ex.Message}");
                                WriteToLog($"Отправлен ответ: ERROR:PrintersNotSet - {ex.Message}");
                            }
                        }
                        else
                        {
                            writer.WriteLine("ERROR:UnknownCommand");
                            WriteToLog($"Отправлен ответ: ERROR:UnknownCommand");
                        }
                    }
                    WriteToLog("Соединение с клиентом закрыто.");
                }
            }
            catch (IOException ex)
            {
                WriteToLog($"Ошибка при работе с TCP-клиентом: {ex.Message}");
            }
            catch (Exception ex)
            {
                WriteToLog($"Непредвиденная ошибка при обработке TCP-клиента: {ex.Message}");
            }
            finally
            {
                client.Close();
                WriteToLog("TCP-клиент закрыт.");
            }
        }
    }
}

public class Settings
{
    public List<string> Printers { get; set; }
    public decimal Interval { get; set; }
}