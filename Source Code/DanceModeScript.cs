using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using LemonUI;
using LemonUI.Menus;

namespace DanceMode
{
    /// <summary>
    /// Основной класс мода DanceMode
    /// Мод позволяет добавлять педов в список и запускать их танцевать синхронно
    /// Управление: Y — открыть/закрыть меню, Backspace — назад, F5 — отладка
    /// </summary>
    public class DanceModeScript : Script
    {
        #region Fields

        // Главное меню
        private NativeMenu _mainMenu;
        private NativeItem _addPedItem;
        private NativeItem _selectDanceItem;
        private NativeItem _clearListItem;
        private NativeMenu _danceMenu;
        
        // Пункты меню для сохранения
        private NativeItem _saveListItem;
        private NativeItem _loadAutoSaveItem;

        // Список выбранных педов (храним handles для потокобезопасности)
        private readonly List<int> _selectedPedHandles = new List<int>();
        private readonly object _pedLock = new object();

        // Флаги для применения танца (volatile для корректной работы между потоками)
        private volatile bool _applyDanceRequested = false;
        private int _danceToApplyIndex = 0;

        // Загрузка анимации (volatile для корректной работы между потоками)
        private volatile bool _isLoadingAnimDict = false;
        private string _animDictToLoad = null;
        private uint _loadStartTime = 0;
        private const int LoadTimeoutMs = 5000;

        // Список доступных танцев (анимаций)
        private readonly List<DanceAnimation> _danceAnimations = new List<DanceAnimation>();

        // Текущая выбранная анимация для применения
        private DanceAnimation _selectedDance;

        // Для поддержания анимации
        private bool _danceIsActive = false;

        // Для ротации логов
        private const long MaxLogFileSizeBytes = 5 * 1024 * 1024; // 5 MB

        // Логирование в файл
        private static readonly string LogFilePath = Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
            "DanceModeLogs.txt");

        #endregion

        #region Constructor

        public DanceModeScript()
        {
            try
            {
                // Логируем запуск
                LogInfo("=== Dance Mode запущен ===");
                LogInfo($"Путь к файлу логов: {LogFilePath}");

                // Валидация и инициализация анимаций
                InitializeValidAnimations();

                InitializeMenus();

                // Подписка на события
                KeyDown += OnKeyDown;
                Tick += OnTick;
                Aborted += OnAborted;

                // Показываем уведомление об успешном запуске
                ShowStartupNotification();

                // Пробуем загрузить автосохранение
                TryLoadAutoSave();

                LogInfo($"Dance Mode инициализирован. Доступно танцев: {_danceAnimations.Count}");
            }
            catch (Exception ex)
            {
                LogError($"Ошибка в конструкторе: {ex.Message}");
                LogError($"Stack trace: {ex.StackTrace}");
            }
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Валидация анимаций — загрузка из FavouriteAnims.xml + проверка что anim dict существует в игре
        /// </summary>
        private void InitializeValidAnimations()
        {
            var allAnimations = LoadFavoriteAnimations();

            int validCount = 0;
            int invalidCount = 0;

            foreach (var anim in allAnimations)
            {
                // Проверяем существование anim dict
                bool exists = Function.Call<bool>(Hash.DOES_ANIM_DICT_EXIST, anim.Dictionary);
                
                if (exists)
                {
                    _danceAnimations.Add(anim);
                    validCount++;
                }
                else
                {
                    // Всё равно добавляем — может dict загрузится позже
                    _danceAnimations.Add(anim);
                    invalidCount++;
                    LogDebug($"[ПРЕДУПРЕЖДЕНИЕ] dict не найден при старте: {anim.Dictionary} (будет запрошен при использовании)");
                }
            }

            LogInfo($"=== Результат валидации ===");
            LogInfo($"Всего в XML: {allAnimations.Count}");
            LogInfo($"Добавлено: {_danceAnimations.Count}");
            LogInfo($"С предупреждениями: {invalidCount}");

            if (_danceAnimations.Count == 0)
            {
                LogError("НИ ОДНА анимация не найдена! Мод не будет работать корректно.");
            }
            else
            {
                // Лог первых 5 для проверки
                for (int i = 0; i < Math.Min(5, _danceAnimations.Count); i++)
                {
                    LogInfo($"  [{i}] {_danceAnimations[i].Dictionary}");
                }
            }
        }

        /// <summary>
        /// Загрузка анимаций из XML файла FavouriteAnims.xml
        /// </summary>
        private List<DanceAnimation> LoadFavoriteAnimations()
        {
            var animations = new List<DanceAnimation>();

            try
            {
                // Ищем FavouriteAnims.xml в нескольких местах (приоритет по порядку)
                string xmlPath = FindFavoriteAnimsFile();

                if (xmlPath == null || !File.Exists(xmlPath))
                {
                    LogError("FavouriteAnims.xml не найден ни в одном из стандартных расположений");
                    LogInfo("Используются резервные анимации");
                    return GetFallbackAnimations();
                }

                LogInfo($"Найден FavouriteAnims.xml: {xmlPath}");

                // Простая парсинг XML без System.Xml (чтобы избежать проблем с зависимостями)
                string xmlContent = File.ReadAllText(xmlPath);

                // Извлекаем все <Anim dict="..." name="..." />
                int pos = 0;
                while ((pos = xmlContent.IndexOf("<Anim ", pos)) >= 0)
                {
                    int dictStart = xmlContent.IndexOf("dict=\"", pos);
                    int nameStart = xmlContent.IndexOf("name=\"", pos);

                    if (dictStart < 0 || nameStart < 0) break;

                    dictStart += 6; // длина 'dict="'
                    nameStart += 6; // длина 'name="'

                    int dictEnd = xmlContent.IndexOf("\"", dictStart);
                    int nameEnd = xmlContent.IndexOf("\"", nameStart);

                    if (dictEnd < 0 || nameEnd < 0) break;

                    string dict = xmlContent.Substring(dictStart, dictEnd - dictStart);
                    string name = xmlContent.Substring(nameStart, nameEnd - nameStart);

                    // Создаём отображаемое имя (последняя часть dict + name)
                    string displayName = CreateDisplayName(dict, name);

                    animations.Add(new DanceAnimation(dict, name, displayName));

                    pos = nameEnd + 1;
                }

                LogInfo($"Загружено {animations.Count} анимаций из FavouriteAnims.xml");
            }
            catch (Exception ex)
            {
                LogError($"Ошибка загрузки FavouriteAnims.xml: {ex.Message}");
                LogInfo("Используются резервные анимации");
                return GetFallbackAnimations();
            }

            return animations;
        }

        /// <summary>
        /// Поиск FavouriteAnims.xml в стандартных расположениях
        /// </summary>
        private string FindFavoriteAnimsFile()
        {
            // Получаем путь к папке где лежит DLL (обычно GTA V/scripts/)
            string dllPath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";

            // Получаем корневую папку GTA V (на уровень выше scripts/)
            string gtaRoot = Path.GetDirectoryName(dllPath);

            // Варианты поиска (в порядке приоритета):
            var searchPaths = new List<string>
            {
                // 1. В той же папке что и DLL (GTA V/scripts/FavouriteAnims.xml)
                Path.Combine(dllPath, "FavouriteAnims.xml"),

                // 2. В папке menyooStuff (GTA V/menyooStuff/FavouriteAnims.xml)
                Path.Combine(gtaRoot ?? ".", "menyooStuff", "FavouriteAnims.xml"),

                // 3. В корне GTA V (GTA V/FavouriteAnims.xml)
                Path.Combine(gtaRoot ?? ".", "FavouriteAnims.xml"),

                // 4. В папке scripts (если DLL в подпапке)
                Path.Combine(dllPath, "scripts", "FavouriteAnims.xml"),

                // 5. Альтернативный путь Menyoo (GTA V/menyooStuff/PedAnims/FavouriteAnims.xml)
                Path.Combine(gtaRoot ?? ".", "menyooStuff", "PedAnims", "FavouriteAnims.xml"),
            };

            foreach (string path in searchPaths)
            {
                if (File.Exists(path))
                {
                    LogInfo($"Найден FavouriteAnims.xml: {path}");
                    return path;
                }
            }

            // Если ничего не найдено, возвращаем первый путь для логирования
            return searchPaths[0];
        }

        /// <summary>
        /// Создание отображаемого имени для анимации
        /// </summary>
        private string CreateDisplayName(string dict, string name)
        {
            // Извлекаем последнюю значимую часть из dict
            string[] parts = dict.Split('@');
            string lastPart = parts.Length > 2 ? parts[parts.Length - 2] : parts[0];
            
            // Убираем префиксы
            lastPart = lastPart.Replace("amb@", "")
                              .Replace("mini@", "")
                              .Replace("anim@", "")
                              .Replace("move@", "");
            
            // Берём первые 20 символов
            if (lastPart.Length > 20)
                lastPart = lastPart.Substring(0, 20);
            
            return lastPart;
        }

        /// <summary>
        /// Резервные анимации если XML не найден
        /// </summary>
        private List<DanceAnimation> GetFallbackAnimations()
        {
            return new List<DanceAnimation>
            {
                new DanceAnimation("anim@amb@nightclub@mini@dance@dance_solo@techno_monkey@", "med_center", "Техно-обезьяна"),
                new DanceAnimation("anim@amb@nightclub@mini@dance@dance_solo@shuffle@", "high_left_up", "Шаффл"),
                new DanceAnimation("anim@amb@nightclub@dancers@crowddance_facedj@hi_intensity", "hi_dance_facedj_15_v1_male^6", "Танец к DJ")
            };
        }

        /// <summary>
        /// Инициализация меню через LemonUI
        /// </summary>
        private void InitializeMenus()
        {
            // Главное меню
            _mainMenu = new NativeMenu("Dance Mode", "Главное меню");
            _mainMenu.Description = "Управление танцующими педами";

            _addPedItem = new NativeItem("Добавить педа", "Добавляет педа, на которого наведена камера");
            _addPedItem.AltTitle = "Y";
            _mainMenu.Add(_addPedItem);

            _selectDanceItem = new NativeItem("Выбрать танец", "Открывает меню выбора танца");
            _selectDanceItem.AltTitle = "→";
            _mainMenu.Add(_selectDanceItem);

            _clearListItem = new NativeItem("Очистить список", "Удаляет всех педов из списка");
            _clearListItem.AltTitle = "Delete";
            _mainMenu.Add(_clearListItem);

            _saveListItem = new NativeItem("Сохранить танец", "Автосохранение текущих педов и танца");
            _saveListItem.AltTitle = "Ctrl+S";
            _mainMenu.Add(_saveListItem);

            _loadAutoSaveItem = new NativeItem("Загрузить автосохранение", "Загружает педов и танец из автосохранения");
            UpdateLoadSaveInfo();
            _mainMenu.Add(_loadAutoSaveItem);

            // Меню выбора танца
            _danceMenu = new NativeMenu("Dance Mode", "Выберите танец");
            _danceMenu.Description = "Используйте стрелки для выбора";

            foreach (var dance in _danceAnimations)
            {
                var item = new NativeItem(dance.DisplayName);
                item.Tag = dance;
                _danceMenu.Add(item);
            }

            // Обработчики меню
            _mainMenu.ItemActivated += OnMainMenuItemActivated;
            _danceMenu.ItemActivated += OnDanceMenuItemActivated;
        }

        #endregion

        #region Events

        /// <summary>
        /// Обработчик нажатия клавиш
        /// </summary>
        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                // Открытие/закрытие главного меню
                if (e.KeyCode == Keys.Y)
                {
                    _mainMenu.Visible = !_mainMenu.Visible;
                    _danceMenu.Visible = false;
                    e.SuppressKeyPress = true;
                    return;
                }

                // Backspace для возврата из меню танцев
                if (e.KeyCode == Keys.Back && _danceMenu.Visible)
                {
                    _danceMenu.Visible = false;
                    _mainMenu.Visible = true;
                    e.SuppressKeyPress = true;
                    return;
                }

                // F5 для тестовой проверки
                if (e.KeyCode == Keys.F5)
                {
                    ShowDebugInfo();
                    e.SuppressKeyPress = true;
                    return;
                }

                // Ctrl+S для быстрого сохранения
                if (e.Control && e.KeyCode == Keys.S)
                {
                    ManualSave();
                    e.SuppressKeyPress = true;
                    return;
                }
            }
            catch (Exception ex)
            {
                LogError($"Ошибка в обработчике клавиш: {ex.Message}");
            }
        }

        /// <summary>
        /// Обработчик завершения скрипта
        /// </summary>
        private void OnAborted(object sender, EventArgs e)
        {
            Cleanup();
        }

        /// <summary>
        /// Очистка ресурсов при завершении
        /// </summary>
        private void Cleanup()
        {
            try
            {
                // Автосохранение перед завершением
                if (_selectedPedHandles.Count > 0 && _selectedDance.Dictionary != null)
                {
                    LogInfo("Создание автосохранения...");
                    var peds = GetPedListFromHandles();
                    var danceData = new DanceData
                    {
                        Dictionary = _selectedDance.Dictionary,
                        Name = _selectedDance.Name,
                        DisplayName = _selectedDance.DisplayName
                    };
                    string result = SaveService.AutoSave(danceData, peds);
                    LogInfo(result);
                }

                // Очистка танца со всех педов
                lock (_pedLock)
                {
                    foreach (int handle in _selectedPedHandles)
                    {
                        var ped = Entity.FromHandle(handle) as Ped;
                        if (ped != null && ped.Exists())
                        {
                            Function.Call(Hash.CLEAR_PED_TASKS, ped);
                        }
                    }
                    _selectedPedHandles.Clear();
                }

                // Очистка анимации
                if (_selectedDance.Dictionary != null)
                {
                    Function.Call(Hash.REMOVE_ANIM_DICT, _selectedDance.Dictionary);
                }

                LogInfo("Dance Mode: ресурсы очищены");
            }
            catch (Exception ex)
            {
                LogError($"Ошибка при очистке: {ex.Message}");
            }
        }

        /// <summary>
        /// Попытка загрузки автосохранения при старте
        /// </summary>
        private void TryLoadAutoSave()
        {
            try
            {
                if (!SaveService.HasAutoSave())
                {
                    LogInfo("Автосохранение не найдено");
                    return;
                }

                LogInfo("Найдено автосохранение, загрузка...");
                var saveData = SaveService.LoadAutoSave();

                if (saveData == null || string.IsNullOrEmpty(saveData.DanceDictionary))
                {
                    LogInfo("Автосохранение повреждено или пусто");
                    return;
                }

                // Ищем танец в списке
                int danceIndex = -1;
                for (int i = 0; i < _danceAnimations.Count; i++)
                {
                    if (_danceAnimations[i].Dictionary == saveData.DanceDictionary &&
                        _danceAnimations[i].Name == saveData.DanceName)
                    {
                        danceIndex = i;
                        break;
                    }
                }

                if (danceIndex < 0)
                {
                    LogInfo($"Танец из автосохранения не найден: {saveData.DanceDisplayName}");
                    return;
                }

                // Ищем педов по позициям
                var foundPeds = SaveService.FindPedsByPositions(saveData.PedPositions);

                if (foundPeds.Count == 0)
                {
                    LogInfo($"Педы не найдены по позициям из автосохранения (ожидалось {saveData.PedPositions.Count})");
                    return;
                }

                LogInfo($"Найдено {foundPeds.Count} педов из {saveData.PedPositions.Count}");

                // Добавляем найденных педов в список
                lock (_pedLock)
                {
                    _selectedPedHandles.Clear();
                    foreach (var ped in foundPeds)
                    {
                        if (!_selectedPedHandles.Contains(ped.Handle))
                        {
                            _selectedPedHandles.Add(ped.Handle);
                        }
                    }
                }

                // Применяем танец
                _danceToApplyIndex = danceIndex;
                _applyDanceRequested = true;

                string message = $"Автосохранение загружено: {foundPeds.Count} педов, танец '{saveData.DanceDisplayName}'";
                LogInfo(message);
                ShowNotification(message);
            }
            catch (Exception ex)
            {
                LogError($"Ошибка при загрузке автосохранения: {ex.Message}");
            }
        }

        /// <summary>
        /// Получение списка Ped из handles
        /// </summary>
        private List<Ped> GetPedListFromHandles()
        {
            var peds = new List<Ped>();
            lock (_pedLock)
            {
                foreach (int handle in _selectedPedHandles)
                {
                    var ped = Entity.FromHandle(handle) as Ped;
                    if (ped != null && ped.Exists())
                    {
                        peds.Add(ped);
                    }
                }
            }
            return peds;
        }

        /// <summary>
        /// Обработчик каждого кадра
        /// </summary>
        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                // Отрисовка меню — только активного (исправление конфликта ввода)
                if (_mainMenu.Visible)
                {
                    _mainMenu.Process();
                }
                else if (_danceMenu.Visible)
                {
                    _danceMenu.Process();
                }

                // Чистка мёртвых/удалённых педов
                CleanupDeadPeds();

                // Обновление лейбла для пункта "Добавить педа"
                int pedCount;
                lock (_pedLock)
                {
                    pedCount = _selectedPedHandles.Count;
                }

                if (pedCount > 0)
                {
                    _addPedItem.AltTitle = $"{pedCount} пед(ов)";
                }
                else
                {
                    _addPedItem.AltTitle = "Y";
                }

                // Обработка загрузки анимации
                if (_isLoadingAnimDict)
                {
                    if (Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, _animDictToLoad))
                    {
                        // Анимация загружена, применяем танец
                        _isLoadingAnimDict = false;
                        ApplyDanceToAllPedsInternal(_danceToApplyIndex);
                        _danceIsActive = true;
                    }
                    else
                    {
                        // Проверяем таймаут (с корректной обработкой переполнения TickCount)
                        uint elapsed = unchecked((uint)Environment.TickCount) - _loadStartTime;
                        if (elapsed > LoadTimeoutMs)
                        {
                            _isLoadingAnimDict = false;
                            Function.Call(Hash.REMOVE_ANIM_DICT, _animDictToLoad);
                            LogDebug($"Таймаут загрузки анимации: {_animDictToLoad}");
                            ShowNotification($"~r~Не удалось загрузить анимацию: {_animDictToLoad} (таймаут)");
                        }
                    }
                }

                // Поддержание танца каждый кадр
                if (_danceIsActive && _selectedDance.Dictionary != null)
                {
                    int handleCount;
                    lock (_pedLock)
                    {
                        handleCount = _selectedPedHandles.Count;
                    }

                    if (handleCount > 0)
                    {
                        // Проверяем что анимация всё ещё загружена
                        if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, _selectedDance.Dictionary))
                        {
                            Function.Call(Hash.REQUEST_ANIM_DICT, _selectedDance.Dictionary);
                        }
                        else
                        {
                            // Получаем копию handles для безопасного перебора
                            int[] handlesCopy;
                            lock (_pedLock)
                            {
                                handlesCopy = _selectedPedHandles.ToArray();
                            }

                            // Применяем анимацию для поддержания
                            foreach (int handle in handlesCopy)
                            {
                                var ped = Entity.FromHandle(handle) as Ped;
                                if (ped != null && ped.Exists() && !ped.IsDead)
                                {
                                    // Проверяем играет ли ещё анимация
                                    if (!Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM, ped, _selectedDance.Dictionary, _selectedDance.Name, 3))
                                    {
                                        LogDebug($"Перезапуск анимации для педа #{handle}");
                                        PlayDanceAnim(ped);
                                    }
                                }
                            }
                        }
                    }
                }

                // Применение танца если запрошено
                if (_applyDanceRequested)
                {
                    _applyDanceRequested = false;

                    int handleCount;
                    lock (_pedLock)
                    {
                        handleCount = _selectedPedHandles.Count;
                    }

                    if (handleCount == 0)
                    {
                        ShowNotification("~r~Список педов пуст! Сначала добавьте педов.");
                        return;
                    }

                    var dance = _danceAnimations[_danceToApplyIndex];

                    // Проверяем, загружена ли уже анимация
                    if (Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dance.Dictionary))
                    {
                        LogDebug($"Анимация '{dance.DisplayName}' уже загружена, применяем...");
                        ApplyDanceToAllPedsInternal(_danceToApplyIndex);
                        _danceIsActive = true;
                    }
                    else
                    {
                        // Начинаем загрузку
                        _animDictToLoad = dance.Dictionary;
                        _loadStartTime = unchecked((uint)Environment.TickCount);
                        _isLoadingAnimDict = true;
                        Function.Call(Hash.REQUEST_ANIM_DICT, dance.Dictionary);
                        LogDebug($"Начата загрузка анимации: {dance.Dictionary}");
                        ShowNotification($"Загрузка анимации: {dance.DisplayName}...");
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"Ошибка в Tick: {ex.Message}");
            }
        }

        #endregion

        #region Menu Event Handlers

        /// <summary>
        /// Обработчик активации пункта в главном меню
        /// </summary>
        private void OnMainMenuItemActivated(object sender, ItemActivatedArgs e)
        {
            try
            {
                if (e.Item == _addPedItem)
                {
                    AddTargetPedToList();
                }
                else if (e.Item == _selectDanceItem)
                {
                    _mainMenu.Visible = false;
                    _danceMenu.Visible = true;
                }
                else if (e.Item == _clearListItem)
                {
                    lock (_pedLock)
                    {
                        _selectedPedHandles.Clear();
                    }
                    _danceIsActive = false;
                    ShowNotification("Список педов очищен");
                }
                else if (e.Item == _saveListItem)
                {
                    ManualSave();
                }
                else if (e.Item == _loadAutoSaveItem)
                {
                    ManualLoadAutoSave();
                }
            }
            catch (Exception ex)
            {
                LogError($"Ошибка при выборе в меню: {ex.Message}");
            }
        }

        /// <summary>
        /// Обработчик активации пункта в меню танцев
        /// </summary>
        private void OnDanceMenuItemActivated(object sender, ItemActivatedArgs e)
        {
            try
            {
                if (e.Item?.Tag == null) return;
                
                var dance = (DanceAnimation)e.Item.Tag;
                int index = _danceAnimations.IndexOf(dance);
                if (index >= 0)
                {
                    _danceToApplyIndex = index;
                    _applyDanceRequested = true;
                    _danceMenu.Visible = false;
                    _mainMenu.Visible = false;
                }
            }
            catch (Exception ex)
            {
                LogError($"Ошибка при выборе танца: {ex.Message}");
            }
        }

        #endregion

        #region Save/Load Menu Actions

        /// <summary>
        /// Ручное сохранение
        /// </summary>
        private void ManualSave()
        {
            try
            {
                if (_selectedPedHandles.Count == 0)
                {
                    ShowNotification("~r~Список педов пуст! Нечего сохранять.");
                    return;
                }

                if (_selectedDance.Dictionary == null)
                {
                    ShowNotification("~r~Танец не выбран! Сначала выберите и примените танец.");
                    return;
                }

                var peds = GetPedListFromHandles();
                var danceData = new DanceData
                {
                    Dictionary = _selectedDance.Dictionary,
                    Name = _selectedDance.Name,
                    DisplayName = _selectedDance.DisplayName
                };
                string result = SaveService.AutoSave(danceData, peds);
                ShowNotification(result);
                LogInfo(result);
                UpdateLoadSaveInfo();
            }
            catch (Exception ex)
            {
                LogError($"Ошибка при ручном сохранении: {ex.Message}");
                ShowNotification($"~r~Ошибка сохранения: {ex.Message}");
            }
        }

        /// <summary>
        /// Ручная загрузка автосохранения
        /// </summary>
        private void ManualLoadAutoSave()
        {
            try
            {
                if (!SaveService.HasAutoSave())
                {
                    ShowNotification("~r~Автосохранение не найдено!");
                    return;
                }

                // Очищаем текущий список
                lock (_pedLock)
                {
                    _selectedPedHandles.Clear();
                }
                _danceIsActive = false;

                // Загружаем
                TryLoadAutoSave();
                UpdateLoadSaveInfo();
            }
            catch (Exception ex)
            {
                LogError($"Ошибка при ручной загрузке: {ex.Message}");
                ShowNotification($"~r~Ошибка загрузки: {ex.Message}");
            }
        }

        /// <summary>
        /// Обновление информации о сохранении в меню
        /// </summary>
        private void UpdateLoadSaveInfo()
        {
            try
            {
                if (SaveService.HasAutoSave())
                {
                    string info = SaveService.GetAutoSaveInfo();
                    _loadAutoSaveItem.Description = info;
                }
                else
                {
                    _loadAutoSaveItem.Description = "Нет автосохранения";
                }
            }
            catch
            {
                // Тихая ошибка
            }
        }

        #endregion

        #region Ped Management

        /// <summary>
        /// Добавление педа в список
        /// </summary>
        private void AddTargetPedToList()
        {
            try
            {
                Ped targetPed = GetTargetPed();

                if (targetPed != null && targetPed.Exists())
                {
                    lock (_pedLock)
                    {
                        // Проверка по Handle, а не по ссылке
                        bool alreadyExists = _selectedPedHandles.Any(h => h == targetPed.Handle);

                        if (!alreadyExists)
                        {
                            _selectedPedHandles.Add(targetPed.Handle);
                            ShowNotification($"Пед добавлен: {targetPed.Model.Hash} (Всего: {_selectedPedHandles.Count})");
                        }
                        else
                        {
                            ShowNotification("Этот пед уже в списке");
                        }
                    }
                }
                else
                {
                    ShowNotification("Пед не найден в прицеле");
                }
            }
            catch (Exception ex)
            {
                LogError($"Ошибка при добавлении педа: {ex.Message}");
            }
        }

        /// <summary>
        /// Очистка мёртвых/удалённых педов из списка
        /// </summary>
        private void CleanupDeadPeds()
        {
            lock (_pedLock)
            {
                _selectedPedHandles.RemoveAll(handle =>
                {
                    var ped = Entity.FromHandle(handle) as Ped;
                    return ped == null || !ped.Exists() || ped.IsDead;
                });
            }
        }

        /// <summary>
        /// Показать отладочную информацию (F5)
        /// </summary>
        private void ShowDebugInfo()
        {
            try
            {
                int pedCount;
                lock (_pedLock)
                {
                    pedCount = _selectedPedHandles.Count;
                }

                LogDebug($"=== Debug Info ===");
                LogDebug($"Педов в списке: {pedCount}");
                LogDebug($"Загрузка анимации: {_isLoadingAnimDict}");
                LogDebug($"Анимация для загрузки: {_animDictToLoad ?? "null"}");
                LogDebug($"Танец активен: {_danceIsActive}");
                LogDebug($"Выбранный танец: {_selectedDance.DisplayName ?? "null"}");

                if (pedCount > 0)
                {
                    int[] handlesCopy;
                    lock (_pedLock)
                    {
                        handlesCopy = _selectedPedHandles.ToArray();
                    }

                    for (int i = 0; i < handlesCopy.Length; i++)
                    {
                        var ped = Entity.FromHandle(handlesCopy[i]) as Ped;
                        if (ped != null && ped.Exists())
                        {
                            LogDebug($"  Пед #{i}: Handle={handlesCopy[i]}, Model={ped.Model.Hash}, Alive={ped.IsAlive}, Dead={ped.IsDead}");
                        }
                        else
                        {
                            LogDebug($"  Пед #{i}: Handle={handlesCopy[i]} — НЕ СУЩЕСТВУЕТ");
                        }
                    }
                }

                ShowNotification($"Debug: педов={pedCount}, загрузка={_isLoadingAnimDict}");
            }
            catch (Exception ex)
            {
                LogError($"Ошибка в ShowDebugInfo: {ex.Message}");
            }
        }

        /// <summary>
        /// Получение педа на которого смотрит камера
        /// </summary>
        private Ped GetTargetPed()
        {
            try
            {
                Camera cam = World.RenderingCamera;
                if (cam == null)
                {
                    return null;
                }

                Vector3 source = cam.Position;
                Vector3 direction = cam.Direction;
                Vector3 target = source + direction * 200f;

                // IntersectFlags.Peds — только педы, быстрее и точнее
                RaycastResult ray = World.Raycast(source, target, IntersectFlags.Peds);

                if (ray.DidHit && ray.HitEntity != null)
                {
                    if (ray.HitEntity is Ped ped)
                    {
                        return ped;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                LogError($"Ошибка при получении педа: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Dance Logic

        /// <summary>
        /// Воспроизведение анимации танца для педа
        /// </summary>
        private void PlayDanceAnim(Ped ped)
        {
            if (_selectedDance.Dictionary == null) return;

            // Настройка педа для анимаций
            Function.Call(Hash.SET_PED_CAN_PLAY_AMBIENT_ANIMS, ped, true);
            Function.Call(Hash.SET_PED_CAN_PLAY_AMBIENT_BASE_ANIMS, ped, true);
            Function.Call(Hash.SET_PED_CAN_PLAY_GESTURE_ANIMS, ped, true);

            // Запрашиваем анимацию если не загружена
            if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, _selectedDance.Dictionary))
            {
                Function.Call(Hash.REQUEST_ANIM_DICT, _selectedDance.Dictionary);
            }

            // Очистка задач перед новой анимацией
            Function.Call(Hash.CLEAR_PED_TASKS, ped);

            // Запуск анимации: флаги 1 = Loop (бесконечный цикл)
            // Для ночных клубов нужны полнотельные анимации — без lockX/Y/Z
            Function.Call(Hash.TASK_PLAY_ANIM,
                ped,
                _selectedDance.Dictionary,
                _selectedDance.Name,
                8.0f,        // blendInSpeed
                -8.0f,       // blendOutSpeed
                -1,          // duration (-1 = бесконечно)
                1,           // flags (только Loop)
                0f,          // playbackRate
                false,       // lockX — не блокировать (движение всего тела)
                false,       // lockY — не блокировать
                false        // lockZ — не блокировать
            );
        }

        /// <summary>
        /// Применение танца ко всем педи (внутренний метод, анимация уже загружена)
        /// </summary>
        private void ApplyDanceToAllPedsInternal(int danceIndex)
        {
            try
            {
                int handleCount;
                lock (_pedLock)
                {
                    handleCount = _selectedPedHandles.Count;
                }

                if (handleCount == 0)
                {
                    ShowNotification("~r~Список педов пуст!");
                    return;
                }

                var dance = _danceAnimations[danceIndex];
                _selectedDance = dance;

                LogDebug($"=== Применение анимации ===");
                LogDebug($"Анимация: '{dance.DisplayName}' ({dance.Dictionary}/{dance.Name})");

                int successCount = 0;
                int failCount = 0;

                // Безопасная копия handles
                int[] handlesCopy;
                lock (_pedLock)
                {
                    handlesCopy = _selectedPedHandles.ToArray();
                }

                foreach (int handle in handlesCopy)
                {
                    var ped = Entity.FromHandle(handle) as Ped;

                    if (ped != null && ped.Exists() && !ped.IsDead)
                    {
                        // Проверяем что анимация загружена, если нет — запрашиваем
                        if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dance.Dictionary))
                        {
                            Function.Call(Hash.REQUEST_ANIM_DICT, dance.Dictionary);
                            LogDebug($"Запрошена загрузка анимации: {dance.Dictionary}");
                        }

                        PlayDanceAnim(ped);

                        LogDebug($"TASK_PLAY_ANIM вызван для педа #{handle} ({ped.Model.Hash})");
                        successCount++;
                    }
                    else
                    {
                        LogDebug($"Пед #{handle} не существует или мёртв");
                        failCount++;
                    }
                }

                // Обновляем список удалив мёртвых
                CleanupDeadPeds();

                string message = $"Танец '{dance.DisplayName}' применён к {successCount} педи";
                if (failCount > 0)
                    message += $" ({failCount} мертвы/исчезли)";

                ShowNotification(message);
                LogDebug($"=== Результат: {successCount}/{handleCount} ===");
            }
            catch (Exception ex)
            {
                LogError($"Ошибка при применении танца: {ex.Message}");
                LogError($"Stack trace: {ex.StackTrace}");
                ShowNotification($"~r~Ошибка: {ex.Message}");
            }
        }

        #endregion

        #region Notifications

        /// <summary>
        /// Показ уведомления при запуске
        /// </summary>
        private void ShowStartupNotification()
        {
            try
            {
                GTA.UI.Notification.Show("~g~Dance Mode~w~ запущен успешно!\nНажми ~y~Y~w~ для открытия меню");
            }
            catch (Exception ex)
            {
                LogError($"Ошибка при показе уведомления: {ex.Message}");
            }
        }

        /// <summary>
        /// Показ обычного уведомления
        /// </summary>
        private void ShowNotification(string message)
        {
            try
            {
                GTA.UI.Notification.Show("~b~Dance Mode:~w~ " + message);
            }
            catch (Exception ex)
            {
                LogError($"Ошибка при показе уведомления: {ex.Message}");
            }
        }

        #endregion

        #region Logging

        /// <summary>
        /// Проверка и ротация лог-файла если он слишком большой
        /// </summary>
        private void RotateLogFileIfNeeded()
        {
            try
            {
                if (File.Exists(LogFilePath))
                {
                    var fileInfo = new FileInfo(LogFilePath);
                    if (fileInfo.Length > MaxLogFileSizeBytes)
                    {
                        // Удаляем старый бэкап если есть
                        string oldLogFile = LogFilePath + ".old";
                        if (File.Exists(oldLogFile))
                        {
                            File.Delete(oldLogFile);
                        }
                        // Переименовываем текущий в .old
                        File.Move(LogFilePath, oldLogFile);
                        LogInfo("Лог-файл ротирован (превышен 5MB лимит)");
                    }
                }
            }
            catch
            {
                // Тихая ошибка при ротации
            }
        }

        /// <summary>
        /// Логирование ошибки
        /// </summary>
        private void LogError(string message)
        {
            try
            {
                RotateLogFileIfNeeded();
                string logMessage = $"[{DateTime.Now:HH:mm:ss.fff}] [ERROR] {message}";
                System.Diagnostics.Debug.WriteLine($"[DanceMode Error] {message}");
                File.AppendAllText(LogFilePath, logMessage + Environment.NewLine);
            }
            catch
            {
                // Тихая ошибка
            }
        }

        /// <summary>
        /// Логирование отладочной информации
        /// </summary>
        private void LogDebug(string message)
        {
            try
            {
                RotateLogFileIfNeeded();
                string logMessage = $"[{DateTime.Now:HH:mm:ss.fff}] [DEBUG] {message}";
                System.Diagnostics.Debug.WriteLine($"[DanceMode Debug] {message}");
                File.AppendAllText(LogFilePath, logMessage + Environment.NewLine);
            }
            catch
            {
                // Тихая ошибка
            }
        }

        /// <summary>
        /// Логирование информации
        /// </summary>
        private void LogInfo(string message)
        {
            try
            {
                RotateLogFileIfNeeded();
                string logMessage = $"[{DateTime.Now:HH:mm:ss.fff}] [INFO] {message}";
                System.Diagnostics.Debug.WriteLine($"[DanceMode Info] {message}");
                File.AppendAllText(LogFilePath, logMessage + Environment.NewLine);
            }
            catch
            {
                // Тихая ошибка
            }
        }

        #endregion

        #region Helper Types

        /// <summary>
        /// Информация об анимации танца (immutable record)
        /// </summary>
        private readonly record struct DanceAnimation(string Dictionary, string Name, string DisplayName);

        #endregion
    }
}
