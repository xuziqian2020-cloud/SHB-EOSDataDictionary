using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.ViewModels
{
    /// <summary>XMZADD 20260901 管理共享 EOS 字典的规范缓存、搜索、选中对象和同步展示状态。</summary>
    public sealed class MainViewModel : INotifyPropertyChanged
    {
        private readonly ConnectionProfileStore _profileStore;
        private readonly SnapshotStore _snapshotStore;
        private readonly LocalDictionaryStore _localDictionaryStore;
        private readonly ColumnFilterService _columnFilterService;
        private readonly List<TableMetadata> _allTables;
        private readonly List<TableDisplayModel> _allTableRows;
        private HashSet<string> _projectRelatedObjectNames;
        private readonly HashSet<string> _eosEntityObjectKeys;
        private readonly Dictionary<string, string> _tableColumnFilters;
        private readonly Dictionary<string, string> _fieldColumnFilters;
        private readonly Dictionary<string, string> _relationColumnFilters;
        private readonly HashSet<string> _evidenceKeys;
        private readonly HashSet<string> _logicalRelationDiscoveryKeys;
        private SnapshotData _currentSnapshot;
        private TableDisplayModel _selectedTable;
        private FieldDisplayModel _selectedField;
        private string _tableSearchText;
        private string _fieldSearchText;
        private string _statusLine;
        private string _sourceRoot;
        private RelationDisplayModel _selectedRelation;
        private bool _isBusy;
        private bool _isAiInferenceRunning;
        private bool _isPreview;
        private bool _showAllObjects;
        private bool _showEosEntityObjectsOnly;
        private ConnectionProfile _currentProfile;
        private string _syncStatusText;
        private int _pendingUploadCount;
        private int _pendingApplyCount;
        private string _lastSyncText;
        private bool _isOffline;
        private bool _canOpenStructureMaintenance;

        /// <summary>XMZADD 20260831 使用默认本地存储位置初始化主视图并恢复最近完整快照。</summary>
        public MainViewModel()
            : this(AppPathService.GetLocalDatabasePath(), AppPathService.GetDefaultSourceRoot())
        {
        }

        /// <summary>XMZADD 20260901 使用指定本地存储初始化共享字典主视图，供规范缓存恢复和隔离测试共用。</summary>
        public MainViewModel(string databasePath, string sourceRoot)
        {
            _profileStore = new ConnectionProfileStore(databasePath);
            _snapshotStore = new SnapshotStore(databasePath);
            _localDictionaryStore = new LocalDictionaryStore(databasePath);
            _columnFilterService = new ColumnFilterService();
            _allTables = new List<TableMetadata>();
            _allTableRows = new List<TableDisplayModel>();
            _eosEntityObjectKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _tableColumnFilters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _fieldColumnFilters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _relationColumnFilters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _evidenceKeys = new HashSet<string>(StringComparer.Ordinal);
            _logicalRelationDiscoveryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Profiles = new ObservableCollection<ConnectionProfile>();
            TableRows = new ObservableCollection<TableDisplayModel>();
            FieldRows = new ObservableCollection<FieldDisplayModel>();
            RelationRows = new ObservableCollection<RelationDisplayModel>();
            EvidenceRows = new ObservableCollection<EvidenceDisplayModel>();
            ModuleNodes = new ObservableCollection<ModuleNode>();
            // 完整数据字典默认展示全部物理表，项目证据范围仅作为用户主动选择的辅助视图。
            _showAllObjects = true;
            SourceRoot = string.IsNullOrWhiteSpace(sourceRoot) ? AppPathService.GetDefaultSourceRoot() : sourceRoot;
            VersionText = VersionService.GetDisplayText();
            LoadProfiles();
            InitializeEmptyState();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public ObservableCollection<ConnectionProfile> Profiles { get; private set; }
        public ObservableCollection<TableDisplayModel> TableRows { get; private set; }
        public ObservableCollection<FieldDisplayModel> FieldRows { get; private set; }
        public ObservableCollection<RelationDisplayModel> RelationRows { get; private set; }
        public ObservableCollection<EvidenceDisplayModel> EvidenceRows { get; private set; }
        public ObservableCollection<ModuleNode> ModuleNodes { get; private set; }
        public string SourceRoot
        {
            get { return _sourceRoot; }
            set
            {
                _sourceRoot = value;
                OnPropertyChanged("SourceRoot");
            }
        }
        public string VersionText { get; private set; }
        public ConnectionProfile CurrentProfile
        {
            get { return _currentProfile; }
            private set
            {
                _currentProfile = value;
                OnPropertyChanged("CurrentProfile");
            }
        }
        public string CurrentConnectionText { get; private set; }
        public string ConnectionStatusText { get; private set; }
        public string CurrentDatabaseText { get; private set; }
        public string CurrentObjectText { get; private set; }
        public string CurrentObjectSubText { get; private set; }
        public string CurrentEvidenceText { get; private set; }
        public string BannerText { get; private set; }
        public string TableCountText { get; private set; }
        public string FieldCountText { get; private set; }
        public string RelationDetailText { get; private set; }
        /// <summary>XMZADD 20260901 提供数据表空选状态的统一中文标题，避免界面继续出现视图提示。</summary>
        public string EmptyObjectTitle { get { return "请选择一个数据表"; } }
        /// <summary>XMZADD 20260901 提供 GitHub 共享字典当前同步阶段，供主界面替代数据库连接状态。</summary>
        public string SyncStatusText
        {
            get { return _syncStatusText; }
            internal set
            {
                _syncStatusText = value;
                OnPropertyChanged("SyncStatusText");
            }
        }
        /// <summary>XMZADD 20260901 提供尚未上传到共享仓库的本地修改数量，提醒用户离线积压状态。</summary>
        public int PendingUploadCount
        {
            get { return _pendingUploadCount; }
            internal set
            {
                _pendingUploadCount = value;
                OnPropertyChanged("PendingUploadCount");
            }
        }
        /// <summary>XMZADD 20260903 提供已上传 GitHub 且等待远端修订收录的本地修改数量，区分提交与生效。</summary>
        public int PendingApplyCount
        {
            get { return _pendingApplyCount; }
            internal set
            {
                _pendingApplyCount = value;
                OnPropertyChanged("PendingApplyCount");
            }
        }
        /// <summary>XMZADD 20260901 提供最近一次成功同步时间，便于用户判断本地规范缓存新鲜度。</summary>
        public string LastSyncText
        {
            get { return _lastSyncText; }
            internal set
            {
                _lastSyncText = value;
                OnPropertyChanged("LastSyncText");
            }
        }
        /// <summary>XMZADD 20260901 标识当前是否只使用本地规范缓存，避免离线状态被误认为已联网。</summary>
        public bool IsOffline
        {
            get { return _isOffline; }
            internal set
            {
                _isOffline = value;
                OnPropertyChanged("IsOffline");
            }
        }
        /// <summary>XMZADD 20260901 标识当前 GitHub 身份和本机配置是否允许进入只读结构维护流程。</summary>
        public bool CanOpenStructureMaintenance
        {
            get { return _canOpenStructureMaintenance; }
            internal set
            {
                _canOpenStructureMaintenance = value;
                OnPropertyChanged("CanOpenStructureMaintenance");
            }
        }
        /// <summary>XMZADD 20260831 控制对象目录是否突破项目相关范围显示全部数据库对象。</summary>
        public bool ShowAllObjects
        {
            get { return _showAllObjects; }
            set
            {
                if (_showAllObjects == value)
                {
                    return;
                }

                _showAllObjects = value;
                OnPropertyChanged("ShowAllObjects");
                if (value && _showEosEntityObjectsOnly)
                {
                    // 全量范围和实体范围代表两种互斥口径，避免用户误以为两个条件会做交集。
                    _showEosEntityObjectsOnly = false;
                    OnPropertyChanged("ShowEosEntityObjectsOnly");
                }
                SearchTables(_tableSearchText);
            }
        }
        /// <summary>XMZADD 20260903 仅展示能够追溯到 EOS 源码实体类的物理表，避免名称推测混入实体范围。</summary>
        public bool ShowEosEntityObjectsOnly
        {
            get { return _showEosEntityObjectsOnly; }
            set
            {
                if (_showEosEntityObjectsOnly == value)
                {
                    return;
                }

                _showEosEntityObjectsOnly = value;
                OnPropertyChanged("ShowEosEntityObjectsOnly");
                if (value && _showAllObjects)
                {
                    // 用户选择实体范围后退出全量范围，使搜索口径始终唯一且可解释。
                    _showAllObjects = false;
                    OnPropertyChanged("ShowAllObjects");
                }
                SearchTables(_tableSearchText);
            }
        }
        public bool IsBusy
        {
            get { return _isBusy; }
            private set
            {
                _isBusy = value;
                OnPropertyChanged("IsBusy");
            }
        }

        /// <summary>XMZADD 20260901 双向维护当前数据表选择，并在通知界面前完成字段和详情上下文切换。</summary>
        public TableDisplayModel SelectedTable
        {
            get { return _selectedTable; }
            set
            {
                _selectedTable = value;
                SelectTable(value);
                OnPropertyChanged("SelectedTable");
            }
        }

        /// <summary>XMZADD 20260901 双向维护当前字段选择，并同步决定字段或数据表编辑目标。</summary>
        public FieldDisplayModel SelectedField
        {
            get { return _selectedField; }
            set
            {
                _selectedField = value;
                OnPropertyChanged("SelectedField");
                OnPropertyChanged("CurrentEditTarget");
            }
        }

        /// <summary>XMZADD 20260901 优先返回选中字段元数据，无字段时返回当前数据表元数据供编辑入口使用。</summary>
        public object CurrentEditTarget
        {
            get
            {
                if (_selectedField != null)
                {
                    return _selectedField.Source;
                }
                return _selectedTable == null ? null : _selectedTable.Source;
            }
        }

        public RelationDisplayModel SelectedRelation
        {
            get { return _selectedRelation; }
            set
            {
                _selectedRelation = value;
                if (value == null)
                {
                    RelationDetailText = "选择一条关系查看字段映射和关系类型。";
                }
                else
                {
                    RelationDetailText = value.ParentTableName + "." + value.ParentFieldName +
                        "  →  " + value.ChildTableName + "." + value.ChildFieldName +
                        "    |    " + value.RelationType + "    |    " + value.StatusText;
                }
                OnPropertyChanged("SelectedRelation");
                OnPropertyChanged("RelationDetailText");
            }
        }

        /// <summary>XMZADD 20260901 重新加载本机连接档案供结构维护使用，主界面连接入口暂时停用。</summary>
        public void LoadProfiles()
        {
            Profiles.Clear();
            IList<ConnectionProfile> profiles = _profileStore.LoadAll();
            for (int i = 0; i < profiles.Count; i++)
            {
                Profiles.Add(profiles[i]);
            }
            OnPropertyChanged("Profiles");
        }

        /// <summary>XMZADD 20260831 将隔离测试快照送入现有筛选流程，不向正式界面提供或加载演示数据。</summary>
        public void LoadSnapshotForTesting(SnapshotData snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }

            ApplySnapshot(snapshot, "测试快照", "测试数据库", false);
        }

        /// <summary>XMZADD 20260901 保存结构维护使用的 EOS 源码目录，主界面源码选择入口暂时停用。</summary>
        public void SetSourceRoot(string sourceRoot)
        {
            SourceRoot = sourceRoot;
            StatusLine = Directory.Exists(SourceRoot)
                ? "源码目录已设置：" + SourceRoot
                : "源码目录不存在，真实连接仍可读取数据库结构，但实体和模块映射不可用。";
        }

        /// <summary>XMZADD 20260901 记录结构维护通过的只读连接，主界面连接入口暂时停用且不以此作为加载来源。</summary>
        public void MarkConnectionValidated(ConnectionProfile profile, string databaseName)
        {
            if (profile == null)
            {
                throw new ArgumentNullException("profile");
            }

            CurrentProfile = profile;
            CurrentConnectionText = profile.Name;
            ConnectionStatusText = "连接成功";
            CurrentDatabaseText = string.IsNullOrWhiteSpace(databaseName) ? profile.Database : databaseName;
            BannerText = "连接已验证 · 点击更新字典读取当前 EOS 数据库结构";
            StatusLine = "连接成功：" + profile.Name + "；当前仍保留上一次完整结构。";
            OnPropertyChanged("CurrentConnectionText");
            OnPropertyChanged("ConnectionStatusText");
            OnPropertyChanged("CurrentDatabaseText");
            OnPropertyChanged("BannerText");
        }

        /// <summary>XMZADD 20260901 为结构维护后台读取只读 EOS 结构并合并源码证据，主界面暂时停用。</summary>
        public async Task LoadProfileAsync(ConnectionProfile profile)
        {
            await LoadProfileAsync(profile, CancellationToken.None);
        }

        /// <summary>XMZADD 20260901 为结构维护读取真实结构并提交本地候选快照，主界面暂时停用此加载入口。</summary>
        public async Task LoadProfileAsync(ConnectionProfile profile, CancellationToken cancellationToken)
        {
            if (profile == null)
            {
                throw new ArgumentNullException("profile");
            }

            IsBusy = true;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                SnapshotData previous = null;
                _snapshotStore.TryLoadLatest(profile.ScopeKey, out previous);
                SchemaDiffResult schemaDiff = null;
                SnapshotData snapshot = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SnapshotData data = new SqlServerMetadataReader().Read(profile, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(SourceRoot, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    MetadataEnrichmentService.Enrich(data, evidence);
                    cancellationToken.ThrowIfCancellationRequested();
                    ApplyAiResults(data, _localDictionaryStore.LoadAiExplanations(profile.ScopeKey));
                    cancellationToken.ThrowIfCancellationRequested();
                    // 人工确认结果优先于本次代码、知识库和 AI 自动推测，刷新结构时不能被覆盖。
                    DictionaryOverrideService.ApplyOverrides(data, _localDictionaryStore, profile.ScopeKey);
                    cancellationToken.ThrowIfCancellationRequested();
                    schemaDiff = new SchemaDiffService().Compare(profile.ScopeKey, previous, data);
                    cancellationToken.ThrowIfCancellationRequested();
                    SnapshotData committedSnapshot = null;
                    CompleteCommittedLoad(
                        () => _snapshotStore.ReplaceScope(profile.ScopeKey, data, cancellationToken),
                        () => committedSnapshot = data);
                    return committedSnapshot;
                }, cancellationToken);
                CurrentProfile = profile;
                ApplySnapshot(snapshot, profile.Name, profile.Database, false);
                BannerText = "已加载真实 EOS 结构 · 完整快照已保存到本地";
                StatusLine = "已连接并加载 " + snapshot.Tables.Count + " 个真实数据库对象；检测到 " + (schemaDiff == null ? 0 : schemaDiff.Changes.Count) + " 项结构变化；源码目录：" + (Directory.Exists(SourceRoot) ? SourceRoot : "不可用");
                OnPropertyChanged("BannerText");
            }
            catch (OperationCanceledException)
            {
                StatusLine = "已取消加载，保留上一次结构。";
                throw;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>XMZADD 20260831 将本地完整快照提交设为发布边界，避免取消请求造成界面与本地缓存不一致。</summary>
        private static void CompleteCommittedLoad(Action persistSnapshot, Action markSnapshotCommitted)
        {
            persistSnapshot();
            markSnapshotCommitted();
        }

        /// <summary>XMZADD 20260901 为结构维护刷新当前只读数据库结构和源码证据，主界面暂时停用。</summary>
        public Task RefreshCurrentAsync(ConnectionProfile profile)
        {
            return LoadProfileAsync(profile);
        }

        /// <summary>XMZADD 20260831 使用唯一默认 AI 配置在后台自动推测当前快照中的未知字段，不阻塞真实结构加载。</summary>
        public Task<int> RunAutomaticAiInferenceAsync()
        {
            return RunAutomaticAiInferenceAsync(CancellationToken.None);
        }

        /// <summary>XMZADD 20260831 使用唯一默认 AI 配置逐批处理真正未知项，并把每批结果独立保存到本机。</summary>
        public async Task<int> RunAutomaticAiInferenceAsync(CancellationToken cancellationToken)
        {
            AiProviderConfiguration provider = _localDictionaryStore.GetDefaultAiProvider();
            if (_currentSnapshot == null || _currentSnapshot.Tables == null || CurrentProfile == null ||
                provider == null || !provider.IsEnabled || string.IsNullOrWhiteSpace(provider.ApiKey) || _isAiInferenceRunning)
            {
                return 0;
            }

            // 运行时再次限制批量大小，兼容升级前已保存的异常配置。
            int batchSize = provider.BatchSize <= 0
                ? 10
                : Math.Min(provider.BatchSize, AiInferenceService.MaximumBatchTargetCount);
            SnapshotData snapshot = _currentSnapshot;
            string scopeKey = CurrentProfile.ScopeKey;
            int completedCount = 0;
            var targetQueue = new AiInferenceTargetQueue(snapshot);
            _isAiInferenceRunning = true;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IList<AiInferenceTarget> targets = targetQueue.Take(batchSize);
                    if (targets.Count == 0)
                    {
                        break;
                    }

                    StatusLine = "AI 正在自动推测 " + targets.Count + " 个未知项（默认配置：" + provider.Name + "）。";
                    AiInferenceBatchResult batch = await Task.Run(() => RunAiInferenceBatch(provider, scopeKey, targets), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (batch.Results.Count > 0)
                    {
                        await Task.Run(() => _localDictionaryStore.SaveAiExplanations(batch.Results), cancellationToken);
                        ApplyAiResults(snapshot, batch.Results);
                        RefreshVisibleAiRows(batch.Results);
                        completedCount += batch.Results.Count;
                    }
                    StatusLine = batch.FailureMessage == null
                        ? "AI 已自动补充 " + completedCount + " 项，结果已独立保存到本机。"
                        : "AI 本批已完成 " + batch.Results.Count + " 项；" + batch.FailureMessage;
                    if (batch.Results.Count == 0 || !string.IsNullOrWhiteSpace(batch.FailureMessage))
                    {
                        break;
                    }
                    await Task.Delay(100, cancellationToken);
                }
                return completedCount;
            }
            finally
            {
                _isAiInferenceRunning = false;
            }
        }

        /// <summary>XMZADD 20260831 在 AI 后台批次完成后刷新当前可见表和字段，使推测结果无需用户再次点击分析按钮即可出现。</summary>
        public void RefreshAfterAiInference(int completedCount)
        {
            if (completedCount <= 0 || _currentSnapshot == null)
            {
                return;
            }

            StatusLine = "AI 已自动推测 " + completedCount + " 个未知字段；结果标记为 AI推测，可在后续人工维护中覆盖。";
        }

        /// <summary>XMZADD 20260901 按中文、英文表名和模块候选过滤数据表，并复用默认项目范围缓存。</summary>
        public void SearchTables(string text)
        {
            RebuildTableRows(text, true);
        }

        /// <summary>XMZADD 20260901 重建数据表显示行并按稳定键恢复集合内选中实例，资源跳转可延后选择目标以避免重复刷新。</summary>
        private void RebuildTableRows(string text, bool restoreSelection)
        {
            string selectedSchemaName = restoreSelection && _selectedTable != null ? _selectedTable.Metadata.SchemaName : null;
            string selectedObjectName = restoreSelection && _selectedTable != null ? _selectedTable.ObjectName : null;
            string selectedFieldName = restoreSelection && _selectedField != null ? _selectedField.FieldName : null;
            _tableSearchText = text ?? string.Empty;
            HashSet<string> projectRelatedObjectNames = null;
            bool useAllObjectsFallback = false;
            bool useEntityScope = ShowEosEntityObjectsOnly;
            bool useProjectScope = !ShowAllObjects && !useEntityScope;
            if (useProjectScope)
            {
                projectRelatedObjectNames = GetProjectRelatedObjectNames();
                useAllObjectsFallback = projectRelatedObjectNames.Count == 0;
            }
            var visibleRows = new List<TableDisplayModel>();
            int scopeCount = 0;
            for (int i = 0; i < _allTableRows.Count; i++)
            {
                TableDisplayModel row = _allTableRows[i];
                TableMetadata table = row.Metadata;
                // 当前范围先决定可搜索对象，保证综合搜索和各列筛选不会越过用户选择的口径。
                bool isVisibleByScope = ShowAllObjects ||
                    (useEntityScope && _eosEntityObjectKeys.Contains(MakeTableStableKey(table.SchemaName, table.ObjectName))) ||
                    (useProjectScope && (useAllObjectsFallback || projectRelatedObjectNames.Contains(table.ObjectName ?? string.Empty)));
                if (isVisibleByScope)
                {
                    scopeCount++;
                }
                if (isVisibleByScope && ContainsTableText(table, _tableSearchText) && _columnFilterService.MatchesTable(row, _tableColumnFilters))
                {
                    visibleRows.Add(row);
                }
            }
            // 一次替换结果集合，避免七万级搜索逐行通知 WPF 导致界面卡顿。
            TableRows = new ObservableCollection<TableDisplayModel>(visibleRows);
            OnPropertyChanged("TableRows");
            TableCountText = "当前显示：" + TableRows.Count + " / 当前范围：" + scopeCount +
                             " / 全部：" + _allTableRows.Count + " 张表";
            OnPropertyChanged("TableCountText");
            BuildModuleNodes();

            if (!restoreSelection || string.IsNullOrWhiteSpace(selectedObjectName))
            {
                return;
            }

            TableDisplayModel selectedTable = FindTableDisplayByStableKey(selectedSchemaName, selectedObjectName);
            SelectedTable = selectedTable;
            if (selectedTable != null && !string.IsNullOrWhiteSpace(selectedFieldName))
            {
                SelectedField = FindFieldDisplay(selectedFieldName);
            }
        }

        /// <summary>XMZADD 20260828 按字段名和中文字段名过滤当前表的字段字典。</summary>
        public void SearchFields(string text)
        {
            _fieldSearchText = text ?? string.Empty;
            if (_selectedTable != null)
            {
                PopulateFields(_selectedTable.Metadata);
            }
        }

        /// <summary>XMZADD 20260828 更新表目录指定列的模糊筛选条件并立即刷新可见行。</summary>
        public void SetTableColumnFilter(string columnName, string text)
        {
            UpdateColumnFilter(_tableColumnFilters, columnName, text);
            SearchTables(_tableSearchText);
        }

        /// <summary>XMZADD 20260828 更新字段字典指定列的模糊筛选条件并立即刷新当前表字段。</summary>
        public void SetFieldColumnFilter(string columnName, string text)
        {
            UpdateColumnFilter(_fieldColumnFilters, columnName, text);
            if (_selectedTable != null)
            {
                PopulateFields(_selectedTable.Metadata);
            }
        }

        /// <summary>XMZADD 20260828 更新关联关系指定列的模糊筛选条件并立即刷新当前表关系。</summary>
        public void SetRelationColumnFilter(string columnName, string text)
        {
            UpdateColumnFilter(_relationColumnFilters, columnName, text);
            if (_selectedTable != null)
            {
                PopulateRelations(_selectedTable.Metadata);
            }
        }

        /// <summary>XMZADD 20260901 切换数据表时清除旧字段编辑目标，并一次性刷新字段、关系和来源证据。</summary>
        public void SelectTable(TableDisplayModel table)
        {
            SelectedField = null;
            FieldRows.Clear();
            RelationRows.Clear();
            EvidenceRows.Clear();
            _evidenceKeys.Clear();
            SelectedRelation = null;
            if (table == null)
            {
                CurrentObjectText = EmptyObjectTitle;
                CurrentObjectSubText = "单击资源目录或数据表行查看详情";
                CurrentEvidenceText = "—";
                FieldCountText = "0 个字段";
                NotifyCurrentObject();
                return;
            }

            CurrentObjectText = table.ChineseName;
            CurrentObjectSubText = table.ObjectTypeText + " · " + table.ObjectName;
            CurrentEvidenceText = table.SourceSummary;
            EnsureLogicalRelationsForTable(table.Metadata);
            PopulateFields(table.Metadata);
            PopulateRelations(table.Metadata);
            PopulateEvidence(table.Metadata);
            NotifyCurrentObject();
        }

        /// <summary>XMZADD 20260903 首次查看某表时才补充其双向推测关系，完整字段快照启动期间不再批量膨胀内存。</summary>
        private void EnsureLogicalRelationsForTable(TableMetadata table)
        {
            if (_currentSnapshot == null || table == null)
            {
                return;
            }
            string key = (table.SchemaName ?? string.Empty) + "\u001F" + (table.ObjectName ?? string.Empty);
            if (!_logicalRelationDiscoveryKeys.Add(key))
            {
                return;
            }
            LogicalRelationDiscoveryService.DiscoverForTable(_currentSnapshot, table);
        }

        /// <summary>XMZADD 20260901 兼容旧界面按表名跳转，并清空详情筛选以完整展示首个匹配数据表。</summary>
        public void NavigateToTable(string objectName)
        {
            NavigateToTableCore(null, objectName, false);
        }

        /// <summary>XMZADD 20260901 按架构名和表名精确处理资源节点跳转，避免跨架构同名表选择错误。</summary>
        public void NavigateToTable(string schemaName, string objectName)
        {
            NavigateToTableCore(schemaName, objectName, true);
        }

        /// <summary>XMZADD 20260901 统一执行资源跳转筛选清理和范围扩展，确保新旧跳转入口保持一致。</summary>
        private void NavigateToTableCore(string schemaName, string objectName, bool useStableKey)
        {
            _tableColumnFilters.Clear();
            _fieldColumnFilters.Clear();
            _relationColumnFilters.Clear();
            _fieldSearchText = string.Empty;
            RebuildTableRows(string.Empty, false);
            TableDisplayModel target = useStableKey
                ? FindTableDisplayByStableKey(schemaName, objectName)
                : FindTableDisplay(objectName);
            if (target == null && !ShowAllObjects)
            {
                // 关系跳转是用户明确意图，默认范围不能阻断其查看二跳依赖表。
                _showEosEntityObjectsOnly = false;
                _showAllObjects = true;
                OnPropertyChanged("ShowEosEntityObjectsOnly");
                OnPropertyChanged("ShowAllObjects");
                RebuildTableRows(string.Empty, false);
                target = useStableKey
                    ? FindTableDisplayByStableKey(schemaName, objectName)
                    : FindTableDisplay(objectName);
            }

            SelectedTable = target;
        }

        /// <summary>XMZADD 20260901 为结构维护恢复指定连接候选快照，主界面暂时停用此连接作用域入口。</summary>
        public bool TryLoadSnapshot(ConnectionProfile profile)
        {
            if (profile == null)
            {
                return false;
            }

            SnapshotData snapshot;
            if (!_snapshotStore.TryLoadLatest(profile.ScopeKey, out snapshot))
            {
                return false;
            }

            IdentifierTranslationService.RepairWeakMetadata(snapshot);
            ApplyAiResults(snapshot, _localDictionaryStore.LoadAiExplanations(profile.ScopeKey));
            DictionaryOverrideService.ApplyOverrides(snapshot, _localDictionaryStore, profile.ScopeKey);
            CurrentProfile = profile;
            ApplySnapshot(snapshot, profile.Name, profile.Database, false);
            BannerText = "已恢复本地快照 · 可继续查看上次已读取的 EOS 结构";
            StatusLine = "已恢复本地快照：" + profile.Name;
            OnPropertyChanged("BannerText");
            return true;
        }

        /// <summary>XMZADD 20260901 记录结构维护只读连接探测失败，主界面连接入口暂时停用。</summary>
        public void MarkConnectionFailed(ConnectionProfile profile)
        {
            CurrentConnectionText = profile == null ? "未选择连接" : profile.Name;
            ConnectionStatusText = "连接失败";
            StatusLine = "连接失败，请检查服务器、账号、密码和网络后重试。";
            OnPropertyChanged("CurrentConnectionText");
            OnPropertyChanged("ConnectionStatusText");
        }

        public string StatusLine
        {
            get { return _statusLine; }
            private set
            {
                _statusLine = value;
                OnPropertyChanged("StatusLine");
            }
        }

        /// <summary>XMZADD 20260901 将规范快照转换为仅含数据表的界面内容，并按稳定键恢复当前表字段上下文。</summary>
        private void ApplySnapshot(SnapshotData snapshot, string connectionName, string databaseName, bool preview)
        {
            string selectedSchemaName = _selectedTable == null ? null : _selectedTable.Metadata.SchemaName;
            string selectedObjectName = _selectedTable == null ? null : _selectedTable.ObjectName;
            string selectedFieldName = _selectedField == null ? null : _selectedField.FieldName;
            _currentSnapshot = snapshot;
            _logicalRelationDiscoveryKeys.Clear();
            _isPreview = preview;
            _allTables.Clear();
            _allTableRows.Clear();
            _eosEntityObjectKeys.Clear();
            for (int i = 0; i < snapshot.Tables.Count; i++)
            {
                TableMetadata table = snapshot.Tables[i];
                // 统一字典只允许物理数据表进入业务浏览区，防止旧缓存中的视图重新出现。
                if (table != null && string.Equals(table.ObjectType, "TABLE", StringComparison.OrdinalIgnoreCase))
                {
                    _allTables.Add(table);
                    _allTableRows.Add(new TableDisplayModel(table));
                    if (HasEosSourceEntity(table))
                    {
                        _eosEntityObjectKeys.Add(MakeTableStableKey(table.SchemaName, table.ObjectName));
                    }
                }
            }
            // 快照已合并本地维护结果，必须重新核验哪些对象属于当前项目范围。
            InvalidateProjectRelatedObjectNames();
            CurrentConnectionText = connectionName;
            CurrentDatabaseText = databaseName;
            BannerText = preview ? "预览模式 · 示例结构仅用于界面预览，不代表实际 EOS 账套" : "已加载共享字典 · 所有名称均带有来源和可信度标记";
            SelectedTable = null;
            SearchTables(_tableSearchText);
            TableDisplayModel selectedTable = FindTableDisplayByStableKey(selectedSchemaName, selectedObjectName);
            if (selectedTable == null && TableRows.Count > 0)
            {
                selectedTable = TableRows[0];
            }
            SelectedTable = selectedTable;
            if (SelectedTable != null && !string.IsNullOrWhiteSpace(selectedFieldName))
            {
                SelectedField = FindFieldDisplay(selectedFieldName);
            }
            NotifyCurrentObject();
            OnPropertyChanged("CurrentConnectionText");
            OnPropertyChanged("CurrentDatabaseText");
            OnPropertyChanged("BannerText");
        }

        /// <summary>XMZADD 20260901 初始化共享字典缓存和同步状态，使主窗口不依赖数据库连接即可先显示。</summary>
        private void InitializeEmptyState()
        {
            CurrentConnectionText = "主界面暂时停用连接";
            ConnectionStatusText = "主界面暂时停用";
            CurrentDatabaseText = "GitHub 共享字典";
            BannerText = "程序已启动 · 正在检查本地规范缓存";
            StatusLine = "正在后台恢复共享字典缓存，界面可继续操作。";
            CurrentObjectText = EmptyObjectTitle;
            CurrentObjectSubText = "本地没有规范缓存时不会加载演示数据";
            CurrentEvidenceText = "—";
            TableCountText = "0 个数据表";
            FieldCountText = "0 个字段";
            SyncStatusText = "等待同步";
            PendingUploadCount = 0;
            PendingApplyCount = 0;
            LastSyncText = "尚未获取";
            IsOffline = true;
            CanOpenStructureMaintenance = false;
            NotifyCurrentObject();
            OnPropertyChanged("CurrentConnectionText");
            OnPropertyChanged("ConnectionStatusText");
            OnPropertyChanged("CurrentDatabaseText");
            OnPropertyChanged("BannerText");
        }

        /// <summary>XMZADD 20260901 在后台恢复固定 GitHub 规范作用域缓存，避免旧连接快照覆盖共享字典。</summary>
        public async Task<bool> RestoreMostRecentSnapshotAsync(CancellationToken cancellationToken)
        {
            IsBusy = true;
            try
            {
                SnapshotData restored = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SnapshotData snapshot;
                    string scopeKey = new DictionaryRepositoryOptions().ScopeKey;
                    if (!_snapshotStore.TryLoadLatest(scopeKey, out snapshot))
                    {
                        return null;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    // 远端尚未收录期间仍保留本机人工结论，避免获取旧修订后界面短暂回退。
                    DictionaryOverrideService.ApplyOverrides(snapshot, _localDictionaryStore, scopeKey);
                    cancellationToken.ThrowIfCancellationRequested();
                    return snapshot;
                }, cancellationToken);
                RefreshLocalSyncSummary();

                if (restored == null)
                {
                    CurrentConnectionText = "主界面暂时停用连接";
                    ConnectionStatusText = "主界面暂时停用";
                    BannerText = "尚无共享字典缓存 · 等待从 GitHub 同步";
                    StatusLine = "尚无本地规范缓存，未加载任何演示数据。";
                    SyncStatusText = "尚无本地缓存";
                    IsOffline = true;
                    OnPropertyChanged("CurrentConnectionText");
                    OnPropertyChanged("ConnectionStatusText");
                    OnPropertyChanged("BannerText");
                    return false;
                }

                ApplySnapshot(restored, "GitHub 共享字典", "SHB", false);
                ConnectionStatusText = "主界面暂时停用";
                BannerText = "已恢复本地规范缓存 · 后台同步后会自动更新";
                StatusLine = "已恢复共享字典缓存，共 " + TableRows.Count + " 个数据表。";
                SyncStatusText = "已加载本地规范缓存";
                OnPropertyChanged("ConnectionStatusText");
                OnPropertyChanged("BannerText");
                return true;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>XMZADD 20260903 直接应用同步器已校验的对象图并叠加本机人工结论，避免超大快照从 SQLite 二次解码。</summary>
        public async Task<bool> ApplySynchronizedSnapshotAsync(
            SnapshotData synchronizedSnapshot,
            CancellationToken cancellationToken)
        {
            if (synchronizedSnapshot == null)
            {
                return false;
            }
            IsBusy = true;
            try
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string scopeKey = new DictionaryRepositoryOptions().ScopeKey;
                    // 远端对象图只代表共享规范，本机尚未生效的人工结论仍须以最高优先级叠加到当前界面。
                    DictionaryOverrideService.ApplyOverrides(
                        synchronizedSnapshot,
                        _localDictionaryStore,
                        scopeKey);
                    cancellationToken.ThrowIfCancellationRequested();
                }, cancellationToken);
                RefreshLocalSyncSummary();
                ApplySnapshot(synchronizedSnapshot, "GitHub 共享字典", "SHB", false);
                ConnectionStatusText = "主界面暂时停用";
                BannerText = "已获取最新共享字典 · 后台将继续自动检查更新";
                StatusLine = "已应用共享字典，共 " + TableRows.Count + " 个数据表。";
                OnPropertyChanged("ConnectionStatusText");
                OnPropertyChanged("BannerText");
                return true;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>XMZADD 20260901 将规范缓存异常转为 GitHub 同步状态，避免普通用户被引导到数据库连接流程。</summary>
        public void MarkSnapshotRestoreFailed(string errorMessage)
        {
            CurrentConnectionText = "主界面暂时停用连接";
            ConnectionStatusText = "主界面暂时停用";
            CurrentDatabaseText = "GitHub 共享字典";
            BannerText = "本地规范缓存无法恢复 · 将从 GitHub 重新同步";
            StatusLine = "本地规范缓存恢复失败：" + (errorMessage ?? "未知错误");
            SyncStatusText = "本地规范缓存恢复失败";
            IsOffline = true;
            OnPropertyChanged("CurrentConnectionText");
            OnPropertyChanged("ConnectionStatusText");
            OnPropertyChanged("CurrentDatabaseText");
            OnPropertyChanged("BannerText");
        }

        /// <summary>XMZADD 20260831 在后台调用默认模型，隔离外部网络延迟使主界面始终保持可操作。</summary>
        private static AiInferenceBatchResult RunAiInferenceBatch(AiProviderConfiguration provider, string scopeKey, IList<AiInferenceTarget> targets)
        {
            var result = new AiInferenceBatchResult();
            var service = new AiInferenceService();
            try
            {
                IList<AiExplanationResult> batchResults = service.InferBatch(provider, scopeKey, targets);
                for (int index = 0; index < batchResults.Count; index++)
                {
                    result.Results.Add(batchResults[index]);
                }
            }
            catch (Exception exception)
            {
                result.FailureMessage = exception.Message;
            }
            return result;
        }

        /// <summary>XMZADD 20260831 通知主视图人工本地字典已保存，使默认项目范围按最新人工证据重新搜索。</summary>
        public void NotifyLocalDictionarySaved()
        {
            RefreshLocalSyncStatus();
            // 人工覆盖可能改变直接项目证据，必须清除旧范围后再刷新默认目录。
            InvalidateProjectRelatedObjectNames();
            SearchTables(_tableSearchText);
        }

        /// <summary>XMZADD 20260903 重新读取本地活跃 outbox 和同步游标，使上传后续获取失败时双计数仍保持真实。</summary>
        public void RefreshLocalSyncStatus()
        {
            RefreshLocalSyncSummary();
        }

        /// <summary>XMZADD 20260831 将 AI 返回结果写入字段语义属性，并明确标记其来源为可追溯的 AI 推测。</summary>
        private static void ApplyAiResults(SnapshotData snapshot, IList<AiExplanationResult> results)
        {
            if (snapshot == null || snapshot.Tables == null || results == null || results.Count == 0)
            {
                return;
            }

            var tableIndex = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
            for (int tableNumber = 0; tableNumber < snapshot.Tables.Count; tableNumber++)
            {
                TableMetadata indexedTable = snapshot.Tables[tableNumber];
                if (indexedTable != null && !string.IsNullOrWhiteSpace(indexedTable.ObjectName) && !tableIndex.ContainsKey(indexedTable.ObjectName))
                {
                    tableIndex.Add(indexedTable.ObjectName, indexedTable);
                }
            }

            for (int resultIndex = 0; resultIndex < results.Count; resultIndex++)
            {
                AiExplanationResult result = results[resultIndex];
                TableMetadata table;
                if (result == null || !tableIndex.TryGetValue(result.ObjectName ?? string.Empty, out table))
                {
                    continue;
                }
                if (string.IsNullOrWhiteSpace(result.FieldName))
                {
                    if (CanApplyAiValue(table.ChineseName))
                    {
                        table.ChineseName = CreateAiMetadataValue(result.ChineseName, result);
                    }
                    if (CanApplyAiValue(table.BusinessMeaning))
                    {
                        table.BusinessMeaning = CreateAiMetadataValue(result.BusinessMeaning, result);
                    }
                    continue;
                }
                if (table.Fields == null)
                {
                    continue;
                }
                for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    FieldMetadata field = table.Fields[fieldIndex];
                    if (field == null || !string.Equals(field.FieldName, result.FieldName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (CanApplyAiValue(field.ChineseName))
                    {
                        field.ChineseName = CreateAiMetadataValue(result.ChineseName, result);
                    }
                    if (CanApplyAiValue(field.BusinessMeaning))
                    {
                        field.BusinessMeaning = CreateAiMetadataValue(result.BusinessMeaning, result);
                    }
                    if (CanApplyAiValue(field.Usage))
                    {
                        field.Usage = CreateAiMetadataValue(result.Usage, result);
                    }
                    break;
                }
            }
        }

        /// <summary>XMZADD 20260831 判断 AI 是否允许填充当前属性，确保数据库、代码和人工证据不会被较低优先级覆盖。</summary>
        private static bool CanApplyAiValue(MetadataValue value)
        {
            return value == null || value.Status == ConfidenceStatus.PendingConfirmation || value.Status == ConfidenceStatus.Guessed ||
                   value.Status == ConfidenceStatus.GuessedConflict || value.Status == ConfidenceStatus.AiGuessed;
        }

        /// <summary>XMZADD 20260831 构造 AI 推测展示值，统一保留置信度、服务商模型和原始推测证据。</summary>
        private static MetadataValue CreateAiMetadataValue(string value, AiExplanationResult result)
        {
            return new MetadataValue
            {
                Value = value ?? string.Empty,
                Status = ConfidenceStatus.AiGuessed,
                ConfidenceScore = result.ConfidenceScore,
                SourceType = "AI推测",
                SourceSummary = result.ProviderName + " / " + result.Model,
                Evidence = result.Evidence
            };
        }

        /// <summary>XMZADD 20260831 只替换本批 AI 影响的可见行，避免每批重建数万行表目录造成新的界面卡顿。</summary>
        private void RefreshVisibleAiRows(IList<AiExplanationResult> results)
        {
            if (results == null)
            {
                return;
            }

            if (results.Count > 0)
            {
                // AI补充的中文和业务语义可能成为项目证据，下一次默认搜索需重新判定可见对象。
                InvalidateProjectRelatedObjectNames();
            }
            for (int resultIndex = 0; resultIndex < results.Count; resultIndex++)
            {
                AiExplanationResult result = results[resultIndex];
                for (int tableIndex = 0; tableIndex < TableRows.Count; tableIndex++)
                {
                    if (string.Equals(TableRows[tableIndex].ObjectName, result.ObjectName, StringComparison.OrdinalIgnoreCase))
                    {
                        TableDisplayModel replacement = new TableDisplayModel(TableRows[tableIndex].Metadata);
                        bool wasSelected = _selectedTable == TableRows[tableIndex];
                        TableRows[tableIndex] = replacement;
                        if (wasSelected)
                        {
                            _selectedTable = replacement;
                            OnPropertyChanged("SelectedTable");
                        }
                        break;
                    }
                }
                if (_selectedTable == null || !string.Equals(_selectedTable.ObjectName, result.ObjectName, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(result.FieldName))
                {
                    continue;
                }
                for (int fieldIndex = 0; fieldIndex < FieldRows.Count; fieldIndex++)
                {
                    if (string.Equals(FieldRows[fieldIndex].FieldName, result.FieldName, StringComparison.OrdinalIgnoreCase))
                    {
                        FieldRows[fieldIndex] = new FieldDisplayModel(FieldRows[fieldIndex].Metadata);
                        break;
                    }
                }
            }
        }

        /// <summary>XMZADD 20260831 在刷新列表后按物理表名找回当前对象，避免后台 AI 更新打断用户的查看上下文。</summary>
        private TableDisplayModel FindTableDisplay(string objectName)
        {
            for (int i = 0; i < TableRows.Count; i++)
            {
                if (string.Equals(TableRows[i].ObjectName, objectName, StringComparison.OrdinalIgnoreCase))
                {
                    return TableRows[i];
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260901 按架构名和物理表名组成的稳定键找回规范快照更新前的选中表。</summary>
        private TableDisplayModel FindTableDisplayByStableKey(string schemaName, string objectName)
        {
            if (string.IsNullOrWhiteSpace(objectName))
            {
                return null;
            }
            for (int index = 0; index < TableRows.Count; index++)
            {
                TableDisplayModel table = TableRows[index];
                if (string.Equals(table.ObjectName, objectName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(table.Metadata.SchemaName ?? string.Empty, schemaName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    return table;
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260903 组合架构和物理表名形成范围缓存键，避免跨架构同名对象互相污染。</summary>
        private static string MakeTableStableKey(string schemaName, string objectName)
        {
            return (schemaName ?? string.Empty) + "\u001F" + (objectName ?? string.Empty);
        }

        /// <summary>XMZADD 20260901 在当前数据表的可见字段中按稳定字段名恢复编辑目标。</summary>
        private FieldDisplayModel FindFieldDisplay(string fieldName)
        {
            for (int index = 0; index < FieldRows.Count; index++)
            {
                if (string.Equals(FieldRows[index].FieldName, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    return FieldRows[index];
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260901 从本地 outbox 和同步游标生成共享字典状态，不触发网络或 EOS 数据库访问。</summary>
        private void RefreshLocalSyncSummary()
        {
            int pendingCount = 0;
            int pendingApplyCount = 0;
            IList<PendingDictionaryOperation> operations = _localDictionaryStore.LoadActivePendingOperations();
            for (int index = 0; index < operations.Count; index++)
            {
                if (operations[index] != null &&
                    (operations[index].Status == PendingOperationStatus.Pending ||
                     operations[index].Status == PendingOperationStatus.Failed))
                {
                    pendingCount++;
                }
                else if (operations[index] != null && operations[index].Status == PendingOperationStatus.Submitted)
                {
                    pendingApplyCount++;
                }
            }
            PendingUploadCount = pendingCount;
            PendingApplyCount = pendingApplyCount;

            DictionaryRepositoryOptions options = new DictionaryRepositoryOptions();
            DictionarySyncState state = _localDictionaryStore.LoadSyncState(options.StateKey);
            LastSyncText = state == null || !state.LastSyncAtUtc.HasValue
                ? "尚未获取"
                : state.LastSyncAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }

        /// <summary>XMZADD 20260831 保存后台 AI 批次的成功结果和单项失败信息，避免错误结果进入数据字典。</summary>
        private sealed class AiInferenceBatchResult
        {
            public AiInferenceBatchResult()
            {
                Results = new List<AiExplanationResult>();
            }

            public IList<AiExplanationResult> Results { get; private set; }
            public string FailureMessage { get; set; }
        }

        private void PopulateFields(TableMetadata table)
        {
            FieldRows.Clear();
            string text = _fieldSearchText ?? string.Empty;
            for (int i = 0; i < table.Fields.Count; i++)
            {
                FieldMetadata field = table.Fields[i];
                FieldDisplayModel row = new FieldDisplayModel(field);
                if (ContainsFieldText(field, text) && _columnFilterService.MatchesField(row, _fieldColumnFilters))
                {
                    FieldRows.Add(row);
                }
            }
            FieldCountText = FieldRows.Count + " 个字段";
            OnPropertyChanged("FieldCountText");
        }

        /// <summary>XMZADD 20260828 按当前逐列条件重建关联关系列表，保证切换表后仍遵循表头筛选。</summary>
        private void PopulateRelations(TableMetadata table)
        {
            RelationRows.Clear();
            for (int i = 0; i < table.Relations.Count; i++)
            {
                RelationDisplayModel row = new RelationDisplayModel(table.Relations[i]);
                if (_columnFilterService.MatchesRelation(row, _relationColumnFilters))
                {
                    RelationRows.Add(row);
                }
            }
        }

        /// <summary>XMZADD 20260831 汇总当前表及其字段关系的原始证据，支持用户按来源路径和规则复核。</summary>
        private void PopulateEvidence(TableMetadata table)
        {
            AddEvidenceRows(table.ChineseName);
            AddEvidenceRows(table.ModuleName);
            AddEvidenceRows(table.EntityName);
            AddEvidenceRows(table.BusinessMeaning);
            AddEvidenceRows(table.Remark);

            if (table.Fields != null)
            {
                for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    FieldMetadata field = table.Fields[fieldIndex];
                    if (field == null)
                    {
                        continue;
                    }

                    // 字段证据属于当前表的可核验上下文，集中展示可避免用户切换多个页面查找来源。
                    AddEvidenceRows(field.ChineseName);
                    AddEvidenceRows(field.EntityPropertyName);
                    AddEvidenceRows(field.BusinessMeaning);
                    AddEvidenceRows(field.Usage);
                    AddEvidenceRows(field.EnumName);
                    AddEvidenceRows(field.RelationSummary);
                    AddEvidenceRows(field.Remark);
                }
            }

            if (table.Relations != null)
            {
                for (int relationIndex = 0; relationIndex < table.Relations.Count; relationIndex++)
                {
                    RelationMetadata relation = table.Relations[relationIndex];
                    if (relation == null)
                    {
                        continue;
                    }

                    AddEvidenceRows(relation.RelationType);
                    AddEvidenceRows(relation.BusinessMeaning);
                    AddEvidenceRows(relation.Remark);
                }
            }
        }

        /// <summary>XMZADD 20260831 追加元数据携带的证据条目，空证据保持来源明细页为空。</summary>
        private void AddEvidenceRows(MetadataValue metadata)
        {
            if (metadata == null || metadata.Evidence == null)
            {
                return;
            }

            for (int evidenceIndex = 0; evidenceIndex < metadata.Evidence.Count; evidenceIndex++)
            {
                EvidenceItem evidence = metadata.Evidence[evidenceIndex];
                if (evidence != null && _evidenceKeys.Add(GetEvidenceKey(evidence)))
                {
                    EvidenceRows.Add(new EvidenceDisplayModel(evidence));
                }
            }
        }

        /// <summary>XMZADD 20260831 生成来源证据内容键，避免同一推断被多个元数据重复展示。</summary>
        private static string GetEvidenceKey(EvidenceItem evidence)
        {
            return (evidence.SourceType ?? string.Empty) + "\u001F" + (evidence.SourcePath ?? string.Empty) + "\u001F" +
                   evidence.SourceLine + "\u001F" + (evidence.RuleName ?? string.Empty) + "\u001F" +
                   (evidence.RawValue ?? string.Empty) + "\u001F" + (evidence.Explanation ?? string.Empty);
        }

        /// <summary>XMZADD 20260831 维护单列筛选值，使空输入能恢复该列全部可见内容。</summary>
        private static void UpdateColumnFilter(Dictionary<string, string> filters, string columnName, string text)
        {
            if (string.IsNullOrWhiteSpace(columnName))
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                filters.Remove(columnName);
                return;
            }
            filters[columnName] = text.Trim();
        }

        /// <summary>XMZADD 20260831 按当前可见对象重建资源树模块节点，确保目录始终与筛选范围一致。</summary>
        private void BuildModuleNodes()
        {
            var moduleNodes = new ObservableCollection<ModuleNode>();
            var moduleMap = new Dictionary<string, ModuleNode>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < TableRows.Count; i++)
            {
                TableDisplayModel table = TableRows[i];
                string moduleName = table.ModuleName;
                ModuleNode node;
                if (!moduleMap.TryGetValue(moduleName, out node))
                {
                    ConfidenceStatus status = table.Metadata.ModuleName == null ? ConfidenceStatus.PendingConfirmation : table.Metadata.ModuleName.Status;
                    node = new ModuleNode(moduleName, status);
                    // 预览模式按已确认设计稿展开前两个业务域，真实连接仍由用户按需展开，避免制造业务优先级。
                    node.IsExpanded = _isPreview && moduleNodes.Count < 2;
                    moduleMap.Add(moduleName, node);
                    moduleNodes.Add(node);
                }
                // 预览选中态只服务于界面还原，不把演示物料误认为当前真实数据库对象。
                table.IsTreeSelected = _isPreview && string.Equals(table.ObjectName, "T_BD_MATERIAL", StringComparison.OrdinalIgnoreCase);
                node.Tables.Add(table);
            }
            // 新模块树完成分组后再整体交给界面，避免模块修改时出现旧、新分组的中间状态。
            ModuleNodes = moduleNodes;
            OnPropertyChanged("ModuleNodes");
        }

        /// <summary>XMZADD 20260831 按快照和证据状态缓存默认项目范围，避免每次输入重复扫描字段关系。</summary>
        private HashSet<string> GetProjectRelatedObjectNames()
        {
            if (_projectRelatedObjectNames == null)
            {
                _projectRelatedObjectNames = BuildProjectRelatedObjectNames();
            }

            return _projectRelatedObjectNames;
        }

        /// <summary>XMZADD 20260831 清除项目范围缓存，使快照、本地维护或 AI 证据变更后重新判定可见对象。</summary>
        private void InvalidateProjectRelatedObjectNames()
        {
            _projectRelatedObjectNames = null;
        }

        /// <summary>XMZADD 20260831 汇集直接项目证据表及其一跳父子关联表，供默认目录保持业务聚焦。</summary>
        private HashSet<string> BuildProjectRelatedObjectNames()
        {
            HashSet<string> objectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int tableIndex = 0; tableIndex < _allTables.Count; tableIndex++)
            {
                TableMetadata table = _allTables[tableIndex];
                // 直接证据是项目对象的起点，关系扩展只围绕这些对象进行。
                if (HasDirectProjectEvidence(table) && !string.IsNullOrWhiteSpace(table.ObjectName))
                {
                    objectNames.Add(table.ObjectName);
                }
            }

            if (objectNames.Count == 0)
            {
                return objectNames;
            }

            // 固化直接证据集合，确保关联扩展严格停在一跳而不会沿规则链继续传播。
            HashSet<string> directEvidenceObjectNames = new HashSet<string>(objectNames, StringComparer.OrdinalIgnoreCase);
            for (int tableIndex = 0; tableIndex < _allTables.Count; tableIndex++)
            {
                TableMetadata table = _allTables[tableIndex];
                if (table == null || table.Relations == null)
                {
                    continue;
                }

                for (int relationIndex = 0; relationIndex < table.Relations.Count; relationIndex++)
                {
                    RelationMetadata relation = table.Relations[relationIndex];
                    if (relation == null)
                    {
                        continue;
                    }

                    // 真实外键和命名推测都能反映项目对象的直接依赖，均保留一跳上下文。
                    if (directEvidenceObjectNames.Contains(relation.ParentTableName ?? string.Empty))
                    {
                        AddObjectName(objectNames, relation.ChildTableName);
                    }
                    if (directEvidenceObjectNames.Contains(relation.ChildTableName ?? string.Empty))
                    {
                        AddObjectName(objectNames, relation.ParentTableName);
                    }
                }
            }

            return objectNames;
        }

        /// <summary>XMZADD 20260831 判断表是否拥有源码、数据库、实体或人工维护等直接项目证据。</summary>
        private static bool HasDirectProjectEvidence(TableMetadata table)
        {
            if (table == null)
            {
                return false;
            }

            if (HasProjectEvidence(table.ChineseName) || HasProjectEvidence(table.ModuleName) ||
                HasProjectEvidence(table.EntityName) || HasProjectEvidence(table.BusinessMeaning) ||
                HasProjectEvidence(table.Remark) || HasConfirmedEntity(table.EntityName))
            {
                return true;
            }

            if (table.Fields != null)
            {
                for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    FieldMetadata field = table.Fields[fieldIndex];
                    if (field != null && (HasProjectEvidence(field.ChineseName) || HasProjectEvidence(field.EntityPropertyName) ||
                        HasProjectEvidence(field.BusinessMeaning) || HasProjectEvidence(field.Usage) ||
                        HasProjectEvidence(field.EnumName) || HasProjectEvidence(field.RelationSummary) ||
                        HasProjectEvidence(field.Remark)))
                    {
                        return true;
                    }
                }
            }

            if (table.Relations != null)
            {
                for (int relationIndex = 0; relationIndex < table.Relations.Count; relationIndex++)
                {
                    RelationMetadata relation = table.Relations[relationIndex];
                    if (relation != null && (HasProjectEvidence(relation.RelationType) ||
                        HasProjectEvidence(relation.BusinessMeaning) || HasProjectEvidence(relation.Remark)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>XMZADD 20260831 识别可追溯的表级或字段级证据状态，排除仅由规则生成的待确认内容。</summary>
        private static bool HasProjectEvidence(MetadataValue value)
        {
            if (value == null)
            {
                return false;
            }

            // 通用字段词典在七万表中会大量重复，只有精确项目条目才可据此提升整张表的范围归属。
            if (value.Status == ConfidenceStatus.KnowledgeBaseEvidence)
            {
                return HasSpecificProjectEvidenceItems(value.Evidence);
            }

            // 结构命名、AI 和普通翻译都只是候选，不能仅凭有一条规则说明就升级整张表的项目归属。
            if (value.Status == ConfidenceStatus.Guessed || value.Status == ConfidenceStatus.GuessedConflict ||
                value.Status == ConfidenceStatus.PendingConfirmation || value.Status == ConfidenceStatus.AiGuessed)
            {
                return false;
            }

            return value.IsManualOverride || value.Status == ConfidenceStatus.LocalOverride ||
                   value.Status == ConfidenceStatus.CodeEvidence || value.Status == ConfidenceStatus.DatabaseEvidence ||
                   value.Status == ConfidenceStatus.Confirmed || HasEvidenceItems(value.Evidence) ||
                   Contains(value.SourceSummary, "源码") || Contains(value.SourceSummary, "SQL Server");
        }

        /// <summary>XMZADD 20260831 识别已有实际实体映射，避免推测占位文本被误判为项目实体。</summary>
        private static bool HasConfirmedEntity(MetadataValue entityName)
        {
            return entityName != null && !string.IsNullOrWhiteSpace(entityName.Value) &&
                   !entityName.Value.StartsWith("推测：", StringComparison.Ordinal);
        }

        /// <summary>XMZADD 20260903 识别带 EOS 实体类规则的源码映射，确保实体范围不接受普通代码名称或翻译猜测。</summary>
        private static bool HasEosSourceEntity(TableMetadata table)
        {
            MetadataValue entityName = table == null ? null : table.EntityName;
            if (entityName == null || string.IsNullOrWhiteSpace(entityName.Value) ||
                entityName.Status != ConfidenceStatus.CodeEvidence)
            {
                return false;
            }
            if (string.Equals(entityName.SourceSummary, "EOS 源码实体类", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (entityName.Evidence == null)
            {
                return false;
            }
            for (int evidenceIndex = 0; evidenceIndex < entityName.Evidence.Count; evidenceIndex++)
            {
                EvidenceItem evidence = entityName.Evidence[evidenceIndex];
                if (evidence != null &&
                    (string.Equals(evidence.RuleName, "KisEntityClass", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(evidence.RuleName, "TableNameProperty", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260831 判断元数据是否携带可在来源证据页展示的原始证据条目。</summary>
        private static bool HasEvidenceItems(IList<EvidenceItem> evidence)
        {
            return evidence != null && evidence.Count > 0;
        }

        /// <summary>XMZADD 20260903 识别精确到项目表字段或业务关系的知识条目，排除跨库通用字段词典。</summary>
        private static bool HasSpecificProjectEvidenceItems(IList<EvidenceItem> evidence)
        {
            if (evidence == null)
            {
                return false;
            }
            for (int evidenceIndex = 0; evidenceIndex < evidence.Count; evidenceIndex++)
            {
                EvidenceItem item = evidence[evidenceIndex];
                string ruleName = item == null ? string.Empty : item.RuleName ?? string.Empty;
                if (ruleName == "ExactProjectTable" || ruleName == "ExactProjectField" ||
                    ruleName == "ProjectModuleHeading" || ruleName == "ProjectTableRemark" ||
                    ruleName == "ProjectFieldEnum" || ruleName == "ProjectEnumItem")
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260831 仅将有效物理表名加入关联范围，避免空值破坏默认目录判断。</summary>
        private static void AddObjectName(HashSet<string> objectNames, string objectName)
        {
            if (!string.IsNullOrWhiteSpace(objectName))
            {
                objectNames.Add(objectName);
            }
        }

        /// <summary>XMZADD 20260903 按表名、架构、中文名、模块和实体名统一匹配资源目录即时搜索。</summary>
        private static bool ContainsTableText(TableMetadata table, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            string query = text.Trim();
            return Contains(table.ObjectName, query) || Contains(table.SchemaName, query) ||
                   Contains(table.ChineseName == null ? string.Empty : table.ChineseName.Value, query) ||
                   Contains(table.ModuleName == null ? string.Empty : table.ModuleName.Value, query) ||
                   Contains(table.EntityName == null ? string.Empty : table.EntityName.Value, query);
        }

        private static bool ContainsFieldText(FieldMetadata field, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            string query = text.Trim();
            return Contains(field.FieldName, query) || Contains(field.OwnerTableName, query) ||
                   Contains(field.DataType, query) || Contains(field.ChineseName == null ? string.Empty : field.ChineseName.Value, query) ||
                   Contains(field.EntityPropertyName == null ? string.Empty : field.EntityPropertyName.Value, query) ||
                   Contains(field.EnumName == null ? string.Empty : field.EnumName.Value, query);
        }

        private static bool Contains(string source, string text)
        {
            return !string.IsNullOrEmpty(source) && source.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void NotifyCurrentObject()
        {
            OnPropertyChanged("CurrentObjectText");
            OnPropertyChanged("CurrentObjectSubText");
            OnPropertyChanged("CurrentEvidenceText");
            OnPropertyChanged("EvidenceRows");
        }

        private void OnPropertyChanged(string propertyName)
        {
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
