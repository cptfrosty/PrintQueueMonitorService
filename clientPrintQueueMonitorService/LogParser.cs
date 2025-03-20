using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace clientPrintQueueMonitorService
{
    internal class LogParser
    {
        private static readonly string LogFormat = @"^(?<Date>\d{2}\.\d{2}\.\d{4})\s(?<Time>\d{2}:\d{2}:\d{2}):\sПринтер\s'[^']+',\sдлина очереди:\s(?<QueueLength>\d+)$";

        public Dictionary<DateTime, int> ParseLogFileAndSummarize(string filePath)
        {
            string tempFilePath = null;
            Dictionary<DateTime, int> queueLengthsByTime = new Dictionary<DateTime, int>();

            try
            {
                tempFilePath = Path.GetTempFileName();
                File.Copy(filePath, tempFilePath, true);

                foreach (string line in File.ReadLines(tempFilePath))
                {
                    PrintLogEntry entry = ParseLogLine(line);
                    if (entry != null)
                    {
                        //  Обрезаем дату и время до минут, чтобы группировать записи по минутам
                        DateTime timeKey = new DateTime(entry.DateTime.Year, entry.DateTime.Month, entry.DateTime.Day, entry.DateTime.Hour, entry.DateTime.Minute, 0);

                        if (queueLengthsByTime.ContainsKey(timeKey))
                        {
                            queueLengthsByTime[timeKey] += entry.QueueLength;
                        }
                        else
                        {
                            queueLengthsByTime[timeKey] = entry.QueueLength;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при чтении/копировании файла: {ex.Message}");
            }
            finally
            {
                if (tempFilePath != null && File.Exists(tempFilePath))
                {
                    try
                    {
                        File.Delete(tempFilePath);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Ошибка при удалении временного файла: {ex.Message}");
                    }
                }
            }

            return queueLengthsByTime;
        }

        private PrintLogEntry ParseLogLine(string line)
        {
            Regex regex = new Regex(LogFormat);
            Match match = regex.Match(line);

            if (match.Success)
            {
                try
                {
                    DateTime dateTime = DateTime.ParseExact(match.Groups["Date"].Value + " " + match.Groups["Time"].Value, "dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
                    int queueLength = int.Parse(match.Groups["QueueLength"].Value);

                    return new PrintLogEntry
                    {
                        DateTime = dateTime,
                        QueueLength = queueLength
                    };
                }
                catch (FormatException ex)
                {
                    Console.WriteLine($"Ошибка при парсинге даты или длины очереди: {ex.Message}");
                    return null;
                }
            }
            else
            {
                return null;
            }
        }
    }
}
public class PrintLogEntry
{
    public DateTime DateTime { get; set; }
    public int QueueLength { get; set; }
}