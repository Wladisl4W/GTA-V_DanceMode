using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GTA;
using GTA.Math;

namespace DanceMode
{
    /// <summary>
    /// Модель данных для сохранения танца
    /// </summary>
    public class DanceSaveData
    {
        public string SaveName { get; set; }
        public string DanceDictionary { get; set; }
        public string DanceName { get; set; }
        public string DanceDisplayName { get; set; }
        public long SaveTimeTicks { get; set; }
        public List<PedPositionData> PedPositions { get; set; } = new List<PedPositionData>();
    }

    /// <summary>
    /// Позиция и модель педа для восстановления
    /// </summary>
    public class PedPositionData
    {
        public int ModelHash { get; set; }
        public float PositionX { get; set; }
        public float PositionY { get; set; }
        public float PositionZ { get; set; }
        public float Heading { get; set; }
    }

    /// <summary>
    /// Данные танца для передачи в SaveService
    /// </summary>
    public class DanceData
    {
        public string Dictionary { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
    }

    /// <summary>
    /// Сервис для сохранения и загрузки состояний танцев
    /// </summary>
    public static class SaveService
    {
        private const string SaveFolderName = "DanceModeSaves";
        private const string AutoSaveFileName = "AutoSave.json";
        private const float PedSearchRadius = 5.0f; // Допуск поиска педа (5 метров)

        private static string SaveFolderPath => Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
            SaveFolderName);

        private static string AutoSaveFilePath => Path.Combine(SaveFolderPath, AutoSaveFileName);

        /// <summary>
        /// Автосохранение текущего состояния
        /// </summary>
        public static string AutoSave(DanceData currentDance, List<Ped> peds)
        {
            try
            {
                Directory.CreateDirectory(SaveFolderPath);

                var saveData = new DanceSaveData
                {
                    SaveName = "Автосохранение",
                    DanceDictionary = currentDance?.Dictionary,
                    DanceName = currentDance?.Name,
                    DanceDisplayName = currentDance?.DisplayName,
                    SaveTimeTicks = DateTime.Now.Ticks
                };

                foreach (var ped in peds)
                {
                    if (ped != null && ped.Exists())
                    {
                        saveData.PedPositions.Add(new PedPositionData
                        {
                            ModelHash = ped.Model.Hash,
                            PositionX = ped.Position.X,
                            PositionY = ped.Position.Y,
                            PositionZ = ped.Position.Z,
                            Heading = ped.Heading
                        });
                    }
                }

                string json = SerializeToJson(saveData);
                File.WriteAllText(AutoSaveFilePath, json);

                return $"Автосохранение создано: {saveData.PedPositions.Count} педов, танец: {saveData.DanceDisplayName}";
            }
            catch (Exception ex)
            {
                return $"Ошибка автосохранения: {ex.Message}";
            }
        }

        /// <summary>
        /// Автозагрузка последнего сохранения
        /// </summary>
        public static DanceSaveData LoadAutoSave()
        {
            try
            {
                if (!File.Exists(AutoSaveFilePath))
                {
                    return null;
                }

                string json = File.ReadAllText(AutoSaveFilePath);
                var saveData = DeserializeFromJson(json);

                return saveData;
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.Show($"~r~Dance Mode: Ошибка загрузки автосохранения: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Поиск педов в мире по сохранённым позициям
        /// </summary>
        public static List<Ped> FindPedsByPositions(List<PedPositionData> positions)
        {
            var foundPeds = new List<Ped>();
            var usedPeds = new HashSet<int>(); // Чтобы не использовать одного педа дважды

            foreach (var position in positions)
            {
                var targetPos = new Vector3(position.PositionX, position.PositionY, position.PositionZ);

                // Получаем все педа в радиусе
                Ped[] nearbyPeds = World.GetNearbyPeds(targetPos, PedSearchRadius * 2);

                foreach (var ped in nearbyPeds)
                {
                    if (ped == null || !ped.Exists() || ped.IsDead)
                        continue;

                    // Пропускаем уже использованных
                    if (usedPeds.Contains(ped.Handle))
                        continue;

                    // Проверяем расстояние до целевой позиции
                    float distance = Vector3.Distance(targetPos, ped.Position);

                    if (distance <= PedSearchRadius)
                    {
                        foundPeds.Add(ped);
                        usedPeds.Add(ped.Handle);
                        break; // Переходим к следующей позиции
                    }
                }
            }

            return foundPeds;
        }

        /// <summary>
        /// Проверка наличия автосохранения
        /// </summary>
        public static bool HasAutoSave()
        {
            return File.Exists(AutoSaveFilePath);
        }

        /// <summary>
        /// Удаление автосохранения
        /// </summary>
        public static void DeleteAutoSave()
        {
            try
            {
                if (File.Exists(AutoSaveFilePath))
                {
                    File.Delete(AutoSaveFilePath);
                }
            }
            catch
            {
                // Тихая ошибка
            }
        }

        /// <summary>
        /// Получение информации об автосохранении для отображения
        /// </summary>
        public static string GetAutoSaveInfo()
        {
            try
            {
                var saveData = LoadAutoSave();
                if (saveData == null)
                    return "Нет автосохранения";

                var saveTime = new DateTime(saveData.SaveTimeTicks);
                return $"Автосохранение: {saveData.DanceDisplayName}, {saveData.PedPositions.Count} педов, {saveTime:dd.MM.yyyy HH:mm}";
            }
            catch
            {
                return "Ошибка чтения автосохранения";
            }
        }

        #region Simple JSON Serialization

        /// <summary>
        /// Простая JSON сериализация без внешних зависимостей
        /// </summary>
        private static string SerializeToJson(DanceSaveData data)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"SaveName\": \"{EscapeJson(data.SaveName)}\",");
            sb.AppendLine($"  \"DanceDictionary\": \"{EscapeJson(data.DanceDictionary)}\",");
            sb.AppendLine($"  \"DanceName\": \"{EscapeJson(data.DanceName)}\",");
            sb.AppendLine($"  \"DanceDisplayName\": \"{EscapeJson(data.DanceDisplayName)}\",");
            sb.AppendLine($"  \"SaveTimeTicks\": {data.SaveTimeTicks},");
            sb.AppendLine("  \"PedPositions\": [");

            for (int i = 0; i < data.PedPositions.Count; i++)
            {
                var ped = data.PedPositions[i];
                sb.AppendLine("    {");
                sb.AppendLine($"      \"ModelHash\": {ped.ModelHash},");
                sb.AppendLine($"      \"PositionX\": {ped.PositionX.ToString("F4")},");
                sb.AppendLine($"      \"PositionY\": {ped.PositionY.ToString("F4")},");
                sb.AppendLine($"      \"PositionZ\": {ped.PositionZ.ToString("F4")},");
                sb.AppendLine($"      \"Heading\": {ped.Heading.ToString("F4")}");
                sb.AppendLine("    }" + (i < data.PedPositions.Count - 1 ? "," : ""));
            }

            sb.AppendLine("  ]");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Простая JSON десериализация
        /// </summary>
        private static DanceSaveData DeserializeFromJson(string json)
        {
            var data = new DanceSaveData();

            // Простая ручная парсерка для нашего формата
            data.SaveName = ExtractStringValue(json, "SaveName");
            data.DanceDictionary = ExtractStringValue(json, "DanceDictionary");
            data.DanceName = ExtractStringValue(json, "DanceName");
            data.DanceDisplayName = ExtractStringValue(json, "DanceDisplayName");
            data.SaveTimeTicks = ExtractLongValue(json, "SaveTimeTicks");

            // Парсим позиции педов
            int posStart = json.IndexOf("\"PedPositions\"");
            if (posStart >= 0)
            {
                int arrayStart = json.IndexOf("[", posStart);
                int arrayEnd = json.IndexOf("]", arrayStart);

                if (arrayStart >= 0 && arrayEnd > arrayStart)
                {
                    string arrayContent = json.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);

                    // Разделяем по объектам { }
                    int objStart = 0;
                    while ((objStart = arrayContent.IndexOf("{", objStart)) >= 0)
                    {
                        int objEnd = arrayContent.IndexOf("}", objStart);
                        if (objEnd < 0) break;

                        string objContent = arrayContent.Substring(objStart, objEnd - objStart + 1);

                        var pedData = new PedPositionData
                        {
                            ModelHash = (int)ExtractLongValue(objContent, "ModelHash"),
                            PositionX = (float)ExtractDoubleValue(objContent, "PositionX"),
                            PositionY = (float)ExtractDoubleValue(objContent, "PositionY"),
                            PositionZ = (float)ExtractDoubleValue(objContent, "PositionZ"),
                            Heading = (float)ExtractDoubleValue(objContent, "Heading")
                        };

                        data.PedPositions.Add(pedData);
                        objStart = objEnd + 1;
                    }
                }
            }

            return data;
        }

        private static string ExtractStringValue(string json, string key)
        {
            string search = $"\"{key}\":";
            int start = json.IndexOf(search);
            if (start < 0) return null;

            int valueStart = json.IndexOf("\"", start + search.Length);
            if (valueStart < 0) return null;

            int valueEnd = json.IndexOf("\"", valueStart + 1);
            if (valueEnd < 0) return null;

            return json.Substring(valueStart + 1, valueEnd - valueStart - 1);
        }

        private static long ExtractLongValue(string json, string key)
        {
            string search = $"\"{key}\":";
            int start = json.IndexOf(search);
            if (start < 0) return 0;

            int valueStart = start + search.Length;
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart]))
                valueStart++;

            int valueEnd = valueStart;
            while (valueEnd < json.Length && (char.IsDigit(json[valueEnd]) || json[valueEnd] == '-'))
                valueEnd++;

            if (valueEnd > valueStart && long.TryParse(json.Substring(valueStart, valueEnd - valueStart), out long value))
                return value;

            return 0;
        }

        private static double ExtractDoubleValue(string json, string key)
        {
            string search = $"\"{key}\":";
            int start = json.IndexOf(search);
            if (start < 0) return 0.0;

            int valueStart = start + search.Length;
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart]))
                valueStart++;

            int valueEnd = valueStart;
            while (valueEnd < json.Length && (char.IsDigit(json[valueEnd]) || json[valueEnd] == '.' || json[valueEnd] == '-'))
                valueEnd++;

            if (valueEnd > valueStart && double.TryParse(json.Substring(valueStart, valueEnd - valueStart), out double value))
                return value;

            return 0.0;
        }

        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            return value.Replace("\\", "\\\\")
                       .Replace("\"", "\\\"")
                       .Replace("\n", "\\n")
                       .Replace("\r", "\\r")
                       .Replace("\t", "\\t");
        }

        #endregion
    }
}
