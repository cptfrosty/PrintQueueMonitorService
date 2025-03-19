using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace serverPrintQueueMonitorService
{
    public partial class ServerForm : Form
    {
        public static LogMessage Log;
        private static string _serverAddress = "127.0.0.1";
        private static int _clientPort = 8005;
        private static int _servicePort = 13000; //  Предположим, что служба слушает здесь

        // Флаг, указывающий, что служба запущена (для простоты)
        private static bool _serviceIsRunning = false;

        //  Добавляем делегаты для обработки команд
        public delegate Task CommandHandler(string command, StreamWriter writer);
        private readonly Dictionary<string, CommandHandler> _commandHandlers = new Dictionary<string, CommandHandler>(); // НЕ static

        private TcpListener _listener;
        private bool _isListening = false;
        private CancellationTokenSource _listenerCancellationTokenSource;

        public ServerForm()
        {
            InitializeComponent();
            // Регистрируем обработчики команд
            Log = new LogMessage(logTextBox);
            RegisterCommandHandlers();
            Task.Run(() => StartListening());
        }

        //  Регистрация обработчиков команд
        private void RegisterCommandHandlers()
        {
            _commandHandlers["GET_PRINTERS"] = HandleGetPrintersCommand;
            _commandHandlers["SET_SETTINGS"] = HandleSetSettingsCommand;
            //  Добавьте другие обработчики команд здесь
        }


        private async Task HandleClient(TcpClient client)
        {
            try
            {
                using (NetworkStream clientStream = client.GetStream())
                using (StreamReader clientReader = new StreamReader(clientStream, Encoding.UTF8))
                using (StreamWriter clientWriter = new StreamWriter(clientStream, Encoding.UTF8) { AutoFlush = true })
                {
                    while (client.Connected)
                    {
                        try
                        {
                            string clientCommand = await clientReader.ReadLineAsync();
                            if (clientCommand == null)
                            {
                                // Клиент отключился
                                break;
                            }
                            Log.Print(clientCommand);
                            string[] commandSplit = clientCommand.Split(';');
                            //  Вызываем ProcessCommand и передаем ему clientWriter
                            Log.Print($"Команда: {commandSplit[0]}");
                            string response = await ProcessCommand(commandSplit);

                            await clientWriter.WriteLineAsync(response);
                        }
                        catch (IOException)
                        {
                            // Ошибка при чтении или записи данных
                            break;
                        }
                        catch (Exception)
                        {
                            // Другие ошибки
                            break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ошибка при подключении к клиенту
            }
            finally
            {
                client.Close();
            }
        }

        private async Task<string> ProcessCommand(string[] command)
        {
            string result = "";
            switch (command[0])
            {
                case "GET_PRINTERS":
                    string[] printers = await GetPrinters();
                    result = string.Join(";", printers);
                    break;
                case "SET_SETTINGS":
                    Log.Print(command.ToString());
                    Settings settings = JsonConvert.DeserializeObject<Settings>(command[1]);
                    SetSettings(settings);
                    break;
                case "GetQueueLength": //  Добавлен пример команды
                    result = "QUEUE_LENGTH:10"; //  Пример значения
                    break;
                default:
                    result = "Unknown command";
                    Log.Print("Команда не распознана");
                    break;
            }
            return result;
        }

        //  Вспомогательный метод для извлечения имени команды
        private static string GetCommandName(string command)
        {
            if (string.IsNullOrEmpty(command)) return string.Empty;
            if (command.Contains(':'))
            {
                return command.Split(':')[0].Trim().ToUpper(); //  Например, "GET_PRINTERS"
            }
            return command.Trim().ToUpper();
        }


        // Обработчик команды GET_PRINTERS
        private async Task HandleGetPrintersCommand(string command, StreamWriter writer)
        {
            try
            {
                string[] printers = await GetPrinters();
                string jsonPrinters = JsonConvert.SerializeObject(printers); // Сериализация в JSON
                await writer.WriteLineAsync($"OK: {jsonPrinters}"); //  Отправляем JSON
            }
            catch (Exception ex)
            {
                await writer.WriteLineAsync($"ERROR: Error getting printers: {ex.Message}");
            }
        }

        // Обработчик команды SET_SETTINGS (пример)
        private static async Task HandleSetSettingsCommand(string command, StreamWriter writer)
        {
            try
            {
                //  Извлекаем параметры из команды.  Предполагаем, что формат:
                //  SET_SETTINGS: {"printers": ["printer1", "printer2"], "interval": 60}
                Log.Print("Попытка десериализации настроек");
                string settingsJson = command.Substring("SET_SETTINGS:".Length).Trim();
                Settings settings = JsonConvert.DeserializeObject<Settings>(settingsJson);

                if (settings != null)
                {
                    Log.Print("Настройки были приняты");
                    SetSettings(settings);
                    await writer.WriteLineAsync("OK: Settings applied");
                }
                else
                {
                    Log.Print("Ошибка: некорректный формат");
                    await writer.WriteLineAsync("ERROR: Invalid settings format");
                }

            }
            catch (Exception ex)
            {
                Log.Print($"ERROR: Error setting settings: {ex.Message}");
                await writer.WriteLineAsync($"ERROR: Error setting settings: {ex.Message}");
            }
        }



        private static void SetSettings(Settings settings)
        {
            if (_serviceIsRunning)
            {
                Log.Print($"Статус службы: {_serviceIsRunning}");
                try
                {
                    using (TcpClient client = new TcpClient())
                    {
                        client.ReceiveTimeout = 10000;
                        client.SendTimeout = 10000;

                        client.Connect(_serverAddress, _servicePort); // Синхронное подключение
                        using (NetworkStream stream = client.GetStream())
                        using (StreamWriter writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            // Устанавливаем принтеры
                            string printersJson = JsonConvert.SerializeObject(settings.Printers);
                            Log.Print($"Отправка SetPrinters: {printersJson}");
                            writer.WriteLine($"SetPrinters:{printersJson}");
                            writer.Flush();

                            string responsePrinters = reader.ReadLine(); // Синхронное чтение
                            Log.Print($"Получен ответ (SetPrinters): {responsePrinters}");

                            if (!responsePrinters.StartsWith("OK:PrintersSet"))
                            {
                                Log.Print($"Ошибка от службы (SetPrinters): {responsePrinters}");
                                return;
                            }

                            // Устанавливаем интервал
                            Log.Print($"Отправка SetInterval: {settings.Interval}");
                            writer.WriteLine($"SetInterval:{settings.Interval}");
                            writer.Flush();

                            string responseInterval = reader.ReadLine();
                            Log.Print($"Получен ответ (SetInterval): {responseInterval}");

                            if (!responseInterval.StartsWith("OK:IntervalSet"))
                            {
                                Log.Print($"Ошибка от службы (SetInterval): {responseInterval}");
                                return;
                            }
                        }
                    }
                }
                catch (SocketException ex)
                {
                    Log.Print($"Ошибка при подключении к службе для SetSettings: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Log.Print($"Ошибка при отправке SetSettings: {ex.Message} - {ex.GetType().FullName} - {ex.StackTrace}");
                }
            }
            else
            {
                Log.Print("Служба не запущена. Невозможно установить настройки.");
            }
        }




        public static async Task<string[]> GetPrinters()
        {
            try
            {
                List<string> physicalPrinters = new List<string>();

                // Используем WMI для получения информации о принтерах
                ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Printer");

                foreach (ManagementObject printer in searcher.Get())
                {
                    // Проверяем, является ли принтер физическим
                    bool isLocal = (bool)printer["Local"]; // Определяет, локальный ли принтер
                    bool isNetwork = (bool)printer["Network"]; // Определяет, сетевой ли принтер
                    string printerName = printer["Name"].ToString();

                    // Фильтруем только физические принтеры (локальные или сетевые, но не виртуальные)
                    if (isLocal || isNetwork)
                    {
                        if (!printerName.Contains("Microsoft Print to PDF") && !printerName.Contains("Microsoft XPS Document Writer") && !printerName.Contains("Send To OneNote"))
                        {
                            physicalPrinters.Add(printerName);
                        }
                    }
                }
                return physicalPrinters.ToArray();
            }
            catch (Exception ex)
            {
                Log.Print($"Ошибка при получении списка физических принтеров: {ex.Message}");
                return new string[0];
            }
        }

        //  Вспомогательный класс для передачи настроек
        public class Settings
        {
            public List<string> Printers { get; set; }
            public decimal Interval { get; set; }
        }

        //  Метод для запуска прослушивания (добавлен для примера)
        public async Task StartListening()
        {
            if (_isListening)
            {
                return;
            }

            _listenerCancellationTokenSource = new CancellationTokenSource();

            try
            {
                IPAddress ipAddress = IPAddress.Loopback;
                _listener = new TcpListener(ipAddress, _clientPort);
                _listener.Start();

                _serviceIsRunning = true;
                _isListening = true;

                while (_isListening && !_listenerCancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        Task<TcpClient> acceptTask = _listener.AcceptTcpClientAsync(); // Получаем Task
                        Task delayTask = Task.Delay(Timeout.Infinite, _listenerCancellationTokenSource.Token); // Создаем Task задержки

                        Task completedTask = await Task.WhenAny(acceptTask, delayTask); // Ждем любой из задач

                        if (completedTask == acceptTask)
                        {
                            // AcceptTcpClientAsync завершился успешно
                            TcpClient client = await acceptTask; // Получаем TcpClient
                            _ = Task.Run(() => HandleClient(client));
                        }
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        // Обработка других исключений
                    }
                }
            }
            catch (SocketException)
            {
                // Обработка SocketException
            }
            catch (Exception)
            {
                // Обработка других исключений
            }
            finally
            {
                StopListeningInternal();
            }
        }

        public void StopListening()
        {
            if (_isListening)
            {
                _listenerCancellationTokenSource.Cancel();
            }
        }

        private void StopListeningInternal()
        {
            if (_isListening)
            {
                _isListening = false;
                _serviceIsRunning = false;

                try
                {
                    _listener?.Stop();
                }
                catch (Exception)
                {
                    // Обработка исключений при остановке
                }
                finally
                {
                    _listener = null;
                    _listenerCancellationTokenSource?.Dispose();
                    _listenerCancellationTokenSource = null;
                }
            }
        }


    }
}
