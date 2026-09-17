using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;
using SHB.EosDataDictionary.ViewModels;

namespace SHB.EosDataDictionary
{
    /// <summary>XMZADD 20260831 提供表和字段业务说明的本机人工维护窗口，物理结构始终保持只读。</summary>
    public sealed class DictionaryEditWindow : Window
    {
        private readonly LocalDictionaryStore _store;
        private readonly string _scopeKey;
        private readonly TableMetadata _table;
        private readonly FieldMetadata _field;
        private readonly Dictionary<string, FrameworkElement> _editors;
        private Border _suggestionPanel;

        /// <summary>XMZADD 20260831 初始化本地字典编辑窗口，人工保存仅更新本机 SQLite 和当前内存快照。</summary>
        public DictionaryEditWindow(string databasePath, string scopeKey, TableMetadata table, FieldMetadata field)
        {
            if (table == null)
            {
                throw new ArgumentNullException("table");
            }

            _store = new LocalDictionaryStore(databasePath);
            _scopeKey = scopeKey ?? string.Empty;
            _table = table;
            _field = field;
            _editors = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
            Title = field == null ? "编辑表字典" : "编辑字段字典";
            Style = Application.Current == null ? null : Application.Current.TryFindResource(typeof(Window)) as Style;
            Width = 700D;
            Height = 720D;
            MinWidth = 620D;
            MinHeight = 600D;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Content = BuildContent();
        }

        /// <summary>XMZADD 20260831 构建物理结构只读区和可维护语义区，避免用户误以为会修改 EOS 表字段。</summary>
        private UIElement BuildContent()
        {
            var root = new Grid { Margin = new Thickness(20D) };
            root.SetResourceReference(Panel.BackgroundProperty, "App.WindowBackgroundBrush");
            MetadataValue suggestion = GetSuggestedName();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12D) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = suggestion == null ? new GridLength(0D) : new GridLength(12D) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1D, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12D) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var physical = new TextBlock
            {
                Text = BuildPhysicalText(),
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(14D, 12D, 14D, 12D)
            };
            physical.SetResourceReference(TextBlock.ForegroundProperty, "App.TextSecondaryBrush");
            var physicalBorder = new Border { Child = physical };
            physicalBorder.Style = TryFindResource("PanelBorderStyle") as Style;
            Grid.SetRow(physicalBorder, 0);
            root.Children.Add(physicalBorder);

            if (suggestion != null)
            {
                _suggestionPanel = BuildSuggestionPanel(suggestion);
                Grid.SetRow(_suggestionPanel, 2);
                root.Children.Add(_suggestionPanel);
            }

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var form = new Grid();
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118D) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1D, GridUnitType.Star) });
            string[] properties = GetEditableProperties(_field != null);
            for (int i = 0; i < properties.Length; i++)
            {
                form.RowDefinitions.Add(new RowDefinition { Height = new GridLength(properties[i] == "BusinessMeaning" || properties[i] == "Usage" || properties[i] == "Remark" ? 94D : 44D) });
                AddEditor(form, i, properties[i]);
            }
            scroll.Content = form;
            var formBorder = new Border { Child = scroll, Padding = new Thickness(16D, 12D, 16D, 12D) };
            formBorder.Style = TryFindResource("PanelBorderStyle") as Style;
            Grid.SetRow(formBorder, 4);
            root.Children.Add(formBorder);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0D, 2D, 0D, 0D) };
            buttons.Children.Add(CreateButton("保存到本机", SaveButton_Click));
            buttons.Children.Add(CreateButton("取消", CancelButton_Click));
            Grid.SetRow(buttons, 6);
            root.Children.Add(buttons);
            return root;
        }

        /// <summary>XMZADD 20260917 构建参考译名审校区，使维护人员先核验证据再决定采纳、修改或拒绝。</summary>
        private Border BuildSuggestionPanel(MetadataValue suggestion)
        {
            var content = new StackPanel();
            var title = new TextBlock
            {
                Text = "参考译名（尚未确认）",
                FontWeight = FontWeights.SemiBold
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "App.TextSecondaryBrush");
            content.Children.Add(title);

            var name = new TextBlock
            {
                Text = suggestion.Value ?? string.Empty,
                FontSize = 18D,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0D, 6D, 0D, 4D),
                TextWrapping = TextWrapping.Wrap
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "App.WarningTextBrush");
            content.Children.Add(name);

            var evidence = new TextBlock
            {
                Text = BuildSuggestionEvidenceText(suggestion),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0D, 0D, 0D, 8D)
            };
            evidence.SetResourceReference(TextBlock.ForegroundProperty, "App.TextMutedBrush");
            content.Children.Add(evidence);

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(CreateButton("采纳到正式名", AcceptSuggestedName_Click));
            actions.Children.Add(CreateButton("拒绝此版本", RejectSuggestedName_Click));
            var hint = new TextBlock
            {
                Text = "采纳后仍可修改，点击底部“保存到本机”才提交正式名称。",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8D, 0D, 0D, 0D)
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "App.TextMutedBrush");
            actions.Children.Add(hint);
            content.Children.Add(actions);

            var panel = new Border
            {
                Child = content,
                Padding = new Thickness(16D, 12D, 16D, 12D)
            };
            panel.Style = TryFindResource("PanelBorderStyle") as Style;
            return panel;
        }

        /// <summary>XMZADD 20260917 将参考译名证据压缩为安全摘要，只展示类型、规则、相对文件和行号。</summary>
        private static string BuildSuggestionEvidenceText(MetadataValue suggestion)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(suggestion.SourceType))
            {
                lines.Add("来源类型：" + suggestion.SourceType.Trim());
            }
            for (int index = 0; suggestion.Evidence != null && index < suggestion.Evidence.Count && index < 4; index++)
            {
                EvidenceItem item = suggestion.Evidence[index];
                if (item == null)
                {
                    continue;
                }
                string location = EvidenceDisplayModel.SanitizeSourcePath(item.SourcePath);
                if (item.SourceLine > 0)
                {
                    location += ":" + item.SourceLine;
                }
                lines.Add("证据：" + (item.SourceType ?? string.Empty) + " · " +
                          (item.RuleName ?? string.Empty) + " · " + location);
            }
            return lines.Count == 0 ? "当前参考译名没有可展示的来源位置，请修改后再确认。" : string.Join("\r\n", lines.ToArray());
        }

        /// <summary>XMZADD 20260917 将参考译名预填到正式名称编辑框，保留人工复核和修改机会且不立即持久化。</summary>
        private bool AcceptSuggestedName()
        {
            MetadataValue suggestion = GetSuggestedName();
            FrameworkElement editor;
            if (suggestion == null || !_editors.TryGetValue("ChineseName", out editor))
            {
                return false;
            }
            var textBox = editor as TextBox;
            if (textBox == null)
            {
                return false;
            }

            textBox.Text = suggestion.Value ?? string.Empty;
            textBox.Focus();
            textBox.SelectAll();
            return true;
        }

        /// <summary>XMZADD 20260917 拒绝当前参考译名证据版本并原子保存指纹事件，避免相同建议刷新后反复出现。</summary>
        private bool RejectSuggestedName()
        {
            MetadataValue suggestion = GetSuggestedName();
            if (suggestion == null)
            {
                return false;
            }

            string fingerprint = new NameSuggestionFingerprintService().CreateFingerprint(suggestion);
            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                return false;
            }

            DateTime createdAtUtc = DateTime.UtcNow;
            string objectKey = (_table.SchemaName ?? string.Empty) + "." + (_table.ObjectName ?? string.Empty);
            string fieldKey = _field == null ? string.Empty : _field.FieldName ?? string.Empty;
            IList<string> rejected = GetRejectedSuggestionFingerprints();
            string rejectedValue = BuildRejectedFingerprintValue(rejected, fingerprint);
            var item = new DictionaryOverride
            {
                ScopeKey = _scopeKey,
                ObjectName = _table.ObjectName ?? string.Empty,
                FieldName = fieldKey,
                ObjectKey = objectKey,
                FieldKey = fieldKey,
                PropertyName = "RejectedSuggestionFingerprint",
                ManualValue = rejectedValue,
                OriginalAutomaticValue = suggestion.Value ?? string.Empty,
                IsLocked = true,
                Remark = "人工拒绝参考译名证据版本",
                UpdatedAt = createdAtUtc
            };
            var batch = new DictionaryChangeBatch
            {
                BatchId = Guid.NewGuid().ToString("N"),
                AuthorGitHubUserId = string.Empty,
                CreatedAtUtc = createdAtUtc
            };
            batch.Overrides.Add(item);
            batch.Operations.Add(new DictionaryChangeOperation
            {
                OperationId = Guid.NewGuid().ToString("N"),
                AuthorGitHubUserId = string.Empty,
                ObjectKey = objectKey,
                FieldKey = fieldKey,
                PropertyName = "SuggestedChineseName",
                OldValue = suggestion.Value ?? string.Empty,
                NewValue = fingerprint,
                ChangeKind = "RejectSuggestion",
                CreatedAtUtc = createdAtUtc
            });

            _store.SaveOverridesWithPendingOperation(batch.Overrides, batch);
            AddRejectedFingerprint(rejected, fingerprint);
            SetSuggestedName(null);
            if (_suggestionPanel != null)
            {
                _suggestionPanel.Visibility = Visibility.Collapsed;
            }
            return true;
        }

        /// <summary>XMZADD 20260917 处理采纳动作但不绕过正式名称的保存事务。</summary>
        private void AcceptSuggestedName_Click(object sender, RoutedEventArgs e)
        {
            AcceptSuggestedName();
        }

        /// <summary>XMZADD 20260917 处理拒绝动作并立即记录可同步的否决决定。</summary>
        private void RejectSuggestedName_Click(object sender, RoutedEventArgs e)
        {
            RejectSuggestedName();
        }

        /// <summary>XMZADD 20260917 读取当前编辑对象仍有效的参考译名。</summary>
        private MetadataValue GetSuggestedName()
        {
            MetadataValue suggestion = _field == null ? _table.SuggestedChineseName : _field.SuggestedChineseName;
            return suggestion == null || string.IsNullOrWhiteSpace(suggestion.Value) ? null : suggestion;
        }

        /// <summary>XMZADD 20260917 返回表或字段的可变否决指纹集合并兼容旧快照中的空集合。</summary>
        private IList<string> GetRejectedSuggestionFingerprints()
        {
            if (_field == null)
            {
                if (_table.RejectedSuggestionFingerprints == null)
                {
                    _table.RejectedSuggestionFingerprints = new List<string>();
                }
                return _table.RejectedSuggestionFingerprints;
            }
            if (_field.RejectedSuggestionFingerprints == null)
            {
                _field.RejectedSuggestionFingerprints = new List<string>();
            }
            return _field.RejectedSuggestionFingerprints;
        }

        /// <summary>XMZADD 20260917 将累积否决指纹编码为稳定本地覆盖值，防止后一次否决覆盖历史决定。</summary>
        private static string BuildRejectedFingerprintValue(IList<string> fingerprints, string current)
        {
            var values = new List<string>();
            for (int index = 0; fingerprints != null && index < fingerprints.Count; index++)
            {
                AddRejectedFingerprint(values, fingerprints[index]);
            }
            AddRejectedFingerprint(values, current);
            return string.Join("\n", values.ToArray());
        }

        /// <summary>XMZADD 20260917 以大小写不敏感方式追加唯一指纹，保证本地重复点击保持幂等。</summary>
        private static void AddRejectedFingerprint(IList<string> fingerprints, string fingerprint)
        {
            if (fingerprints == null || string.IsNullOrWhiteSpace(fingerprint))
            {
                return;
            }
            for (int index = 0; index < fingerprints.Count; index++)
            {
                if (string.Equals(fingerprints[index], fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            fingerprints.Add(fingerprint);
        }

        /// <summary>XMZADD 20260917 清除当前表或字段的参考译名，正式名称不受拒绝动作影响。</summary>
        private void SetSuggestedName(MetadataValue suggestion)
        {
            if (_field == null)
            {
                _table.SuggestedChineseName = suggestion;
            }
            else
            {
                _field.SuggestedChineseName = suggestion;
            }
        }

        /// <summary>XMZADD 20260831 展示不可编辑的真实对象与字段物理信息，明确当前保存不会向 EOS 数据库写入任何内容。</summary>
        private string BuildPhysicalText()
        {
            string text = "仅编辑本机数据字典说明，不会修改 EOS 数据库。\r\n对象：" + (_table.SchemaName ?? string.Empty) + "." + (_table.ObjectName ?? string.Empty) + "（" + (_table.ObjectType ?? string.Empty) + "）";
            if (_field != null)
            {
                text += "\r\n字段：" + _field.FieldName + "；类型：" + _field.DataType + "；长度：" + _field.LengthText + "；键：" + (_field.IsPrimaryKey ? "主键" : (_field.IsForeignKey ? "外键" : "否"));
            }
            return text;
        }

        /// <summary>XMZADD 20260831 添加一个语义属性编辑控件，并加载当前自动或人工展示值供用户确认修改。</summary>
        private void AddEditor(Grid grid, int row, string propertyName)
        {
            var label = new TextBlock { Text = GetDisplayName(propertyName), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0D, 11D, 12D, 0D) };
            Grid.SetRow(label, row);
            grid.Children.Add(label);

            FrameworkElement editor = CreateEditor(propertyName);
            Grid.SetColumn(editor, 1);
            Grid.SetRow(editor, row);
            grid.Children.Add(editor);
            _editors.Add(propertyName, editor);
        }

        /// <summary>XMZADD 20260901 为分类和空表规则创建受约束控件，其余业务说明继续使用文本编辑。</summary>
        private FrameworkElement CreateEditor(string propertyName)
        {
            if (propertyName == "Category")
            {
                var comboBox = new ComboBox
                {
                    Margin = new Thickness(0D, 4D, 0D, 4D),
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                comboBox.Items.Add(DictionaryTableCategory.Business);
                comboBox.Items.Add(DictionaryTableCategory.BaseData);
                comboBox.Items.Add(DictionaryTableCategory.Technical);
                comboBox.Items.Add(DictionaryTableCategory.Excluded);
                if (_table.Category != DictionaryTableCategory.Unclassified)
                {
                    comboBox.SelectedItem = _table.Category;
                }
                return comboBox;
            }
            if (propertyName == "KeepWhenEmpty")
            {
                return new CheckBox
                {
                    IsChecked = _table.KeepWhenEmpty,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0D, 4D, 0D, 4D)
                };
            }

            return new TextBox
            {
                Text = GetMetadataValue(propertyName),
                AcceptsReturn = IsMultiline(propertyName),
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0D, 4D, 0D, 4D),
                Height = Double.NaN,
                VerticalContentAlignment = IsMultiline(propertyName) ? VerticalAlignment.Top : VerticalAlignment.Center
            };
        }

        /// <summary>XMZADD 20260831 保存全部可见人工维护项，使后续重新加载、源码分析和 AI 推测仍以人工结论为最高优先级。</summary>
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SaveChanges();
            DialogResult = true;
            Close();
        }

        /// <summary>XMZADD 20260831 取消当前编辑，不保存任何本地字典修改。</summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>XMZADD 20260901 收集实际变化并在单一事务成功后更新当前内存展示。</summary>
        private bool SaveChanges()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, FrameworkElement> pair in _editors)
            {
                values.Add(pair.Key, GetEditorValue(pair.Key, pair.Value));
            }
            DictionaryChangeBatch batch = CreateChangeBatch(_scopeKey, _table, _field, values, string.Empty);
            if (batch == null)
            {
                return false;
            }

            _store.SaveOverridesWithPendingOperation(batch.Overrides, batch);
            ApplyBatchToMemory(batch);
            return true;
        }

        /// <summary>XMZADD 20260901 将类型化界面值转换为稳定本地字符串，供批次比较和跨版本恢复。</summary>
        private string GetEditorValue(string propertyName, FrameworkElement editor)
        {
            var textBox = editor as TextBox;
            if (textBox != null)
            {
                return textBox.Text ?? string.Empty;
            }
            var comboBox = editor as ComboBox;
            if (comboBox != null)
            {
                return comboBox.SelectedItem == null
                    ? DictionaryTableCategory.Unclassified.ToString()
                    : comboBox.SelectedItem.ToString();
            }
            var checkBox = editor as CheckBox;
            if (checkBox != null)
            {
                return checkBox.IsChecked == true ? bool.TrueString : bool.FalseString;
            }

            return GetCurrentValue(_table, _field, propertyName);
        }

        /// <summary>XMZADD 20260901 在本地事务成功后一次性应用批次结果，失败时保留原内存展示。</summary>
        private void ApplyBatchToMemory(DictionaryChangeBatch batch)
        {
            for (int i = 0; i < batch.Overrides.Count; i++)
            {
                DictionaryOverride item = batch.Overrides[i];
                if (_field == null && item.PropertyName == "Category")
                {
                    DictionaryTableCategory category;
                    if (TryParsePublishedCategory(item.ManualValue, out category))
                    {
                        _table.Category = category;
                    }
                    continue;
                }
                if (_field == null && item.PropertyName == "KeepWhenEmpty")
                {
                    bool keepWhenEmpty;
                    if (bool.TryParse(item.ManualValue, out keepWhenEmpty))
                    {
                        _table.KeepWhenEmpty = keepWhenEmpty;
                    }
                    continue;
                }

                SetMetadata(item.PropertyName, new MetadataValue
                {
                    Value = item.ManualValue ?? string.Empty,
                    Status = ConfidenceStatus.LocalOverride,
                    SourceType = "本地人工维护",
                    SourceSummary = "本地人工维护",
                    OriginalAutomaticValue = item.OriginalAutomaticValue ?? string.Empty,
                    IsManualOverride = true,
                    IsLocked = true,
                    Evidence = new List<EvidenceItem>()
                });
            }
        }

        /// <summary>XMZADD 20260831 读取当前属性的展示值，用于编辑前展示和自动值追溯。</summary>
        private string GetMetadataValue(string propertyName)
        {
            MetadataValue value = GetMetadata(propertyName);
            return value == null ? string.Empty : value.Value ?? string.Empty;
        }

        /// <summary>XMZADD 20260831 按属性名称定位表或字段上的语义元数据，保持人工存储键与内存结构一致。</summary>
        private MetadataValue GetMetadata(string propertyName)
        {
            return GetMetadata(_table, _field, propertyName);
        }

        /// <summary>XMZADD 20260831 将人工维护结果写回对应内存属性，避免用户保存后仍看到旧的自动推测文本。</summary>
        private void SetMetadata(string propertyName, MetadataValue value)
        {
            if (_field == null)
            {
                if (propertyName == "ChineseName") _table.ChineseName = value;
                else if (propertyName == "ModuleName") _table.ModuleName = value;
                else if (propertyName == "EntityName") _table.EntityName = value;
                else if (propertyName == "BusinessMeaning") _table.BusinessMeaning = value;
                else if (propertyName == "Remark") _table.Remark = value;
                return;
            }
            if (propertyName == "ChineseName") _field.ChineseName = value;
            else if (propertyName == "EntityPropertyName") _field.EntityPropertyName = value;
            else if (propertyName == "BusinessMeaning") _field.BusinessMeaning = value;
            else if (propertyName == "Usage") _field.Usage = value;
            else if (propertyName == "RelationSummary") _field.RelationSummary = value;
            else if (propertyName == "Remark") _field.Remark = value;
        }

        /// <summary>XMZADD 20260831 将内部属性名转换为中文编辑标签，方便新成员理解可维护的业务信息。</summary>
        private static string GetDisplayName(string propertyName)
        {
            if (propertyName == "ChineseName") return "中文名称";
            if (propertyName == "ModuleName") return "所属模块";
            if (propertyName == "EntityName") return "实体名称";
            if (propertyName == "EntityPropertyName") return "实体属性";
            if (propertyName == "BusinessMeaning") return "业务含义";
            if (propertyName == "Usage") return "字段用途";
            if (propertyName == "RelationSummary") return "关联说明";
            if (propertyName == "Category") return "表分类";
            if (propertyName == "KeepWhenEmpty") return "空表保留";
            return "备注";
        }

        /// <summary>XMZADD 20260831 为长业务说明启用多行编辑，避免信息被单行控件截断。</summary>
        private static bool IsMultiline(string propertyName)
        {
            return propertyName == "BusinessMeaning" || propertyName == "Usage" || propertyName == "Remark";
        }

        /// <summary>XMZADD 20260901 将实际变化的白名单属性聚合为一个本地事务批次和逐项远程幂等事件。</summary>
        private static DictionaryChangeBatch CreateChangeBatch(
            string scopeKey,
            TableMetadata table,
            FieldMetadata field,
            IDictionary<string, string> values,
            string authorGitHubUserId)
        {
            if (table == null || values == null)
            {
                return null;
            }

            DateTime createdAtUtc = DateTime.UtcNow;
            var batch = new DictionaryChangeBatch
            {
                BatchId = Guid.NewGuid().ToString("N"),
                AuthorGitHubUserId = authorGitHubUserId ?? string.Empty,
                CreatedAtUtc = createdAtUtc
            };
            string[] properties = GetEditableProperties(field != null);
            string objectKey = (table.SchemaName ?? string.Empty) + "." + (table.ObjectName ?? string.Empty);
            string fieldKey = field == null ? string.Empty : field.FieldName ?? string.Empty;
            for (int i = 0; i < properties.Length; i++)
            {
                string propertyName = properties[i];
                string newValue;
                if (!values.TryGetValue(propertyName, out newValue))
                {
                    continue;
                }

                newValue = newValue ?? string.Empty;
                if (field == null && propertyName == "Category")
                {
                    DictionaryTableCategory category;
                    if (!TryParsePublishedCategory(newValue, out category))
                    {
                        // 未分类只表示尚未完成人工判断，不能作为共享字典的最终人工发布值。
                        continue;
                    }
                    newValue = category.ToString();
                }
                if (field == null && propertyName == "KeepWhenEmpty")
                {
                    bool keepWhenEmpty;
                    if (!bool.TryParse(newValue, out keepWhenEmpty))
                    {
                        // 空表收录规则必须由布尔控件给出明确结论，异常文本不进入待上传队列。
                        continue;
                    }
                    newValue = keepWhenEmpty ? bool.TrueString : bool.FalseString;
                }
                string oldValue = GetCurrentValue(table, field, propertyName);
                if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
                {
                    continue;
                }

                MetadataValue current = GetMetadata(table, field, propertyName);
                // 首次人工编辑必须保留编辑前自动展示值，避免空白追溯字段导致刷新后无法还原推测依据。
                string originalAutomaticValue = current == null
                    ? string.Empty
                    : (string.IsNullOrWhiteSpace(current.OriginalAutomaticValue)
                        ? current.Value ?? string.Empty
                        : current.OriginalAutomaticValue);
                var item = new DictionaryOverride
                {
                    ScopeKey = scopeKey ?? string.Empty,
                    ObjectName = table.ObjectName ?? string.Empty,
                    FieldName = fieldKey,
                    ObjectKey = objectKey,
                    FieldKey = fieldKey,
                    PropertyName = propertyName,
                    ManualValue = newValue,
                    OriginalAutomaticValue = originalAutomaticValue,
                    IsLocked = true,
                    UpdatedAt = createdAtUtc
                };
                batch.Overrides.Add(item);
                batch.Operations.Add(new DictionaryChangeOperation
                {
                    OperationId = Guid.NewGuid().ToString("N"),
                    AuthorGitHubUserId = authorGitHubUserId ?? string.Empty,
                    ObjectKey = objectKey,
                    FieldKey = fieldKey,
                    PropertyName = propertyName,
                    OldValue = oldValue,
                    NewValue = newValue,
                    ChangeKind = "Set",
                    CreatedAtUtc = createdAtUtc
                });
            }

            return batch.Operations.Count == 0 ? null : batch;
        }

        /// <summary>XMZADD 20260901 仅接受可人工发布的分类名称，禁止数字枚举值绕过业务白名单。</summary>
        private static bool TryParsePublishedCategory(string value, out DictionaryTableCategory category)
        {
            string normalized = value == null ? string.Empty : value.Trim();
            if (string.Equals(normalized, "Business", StringComparison.OrdinalIgnoreCase))
            {
                category = DictionaryTableCategory.Business;
                return true;
            }
            if (string.Equals(normalized, "BaseData", StringComparison.OrdinalIgnoreCase))
            {
                category = DictionaryTableCategory.BaseData;
                return true;
            }
            if (string.Equals(normalized, "Technical", StringComparison.OrdinalIgnoreCase))
            {
                category = DictionaryTableCategory.Technical;
                return true;
            }
            if (string.Equals(normalized, "Excluded", StringComparison.OrdinalIgnoreCase))
            {
                category = DictionaryTableCategory.Excluded;
                return true;
            }

            category = DictionaryTableCategory.Unclassified;
            return false;
        }

        /// <summary>XMZADD 20260901 返回表或字段允许发布的固定属性白名单，拒绝界面外注入的旧属性。</summary>
        private static string[] GetEditableProperties(bool isField)
        {
            return isField
                ? new[] { "ChineseName", "BusinessMeaning", "Usage", "EntityPropertyName", "Remark", "RelationSummary" }
                : new[] { "ChineseName", "BusinessMeaning", "ModuleName", "EntityName", "Remark", "Category", "KeepWhenEmpty" };
        }

        /// <summary>XMZADD 20260901 读取属性当前业务值以判断用户是否实际修改。</summary>
        private static string GetCurrentValue(TableMetadata table, FieldMetadata field, string propertyName)
        {
            if (field == null && propertyName == "Category")
            {
                return table.Category.ToString();
            }
            if (field == null && propertyName == "KeepWhenEmpty")
            {
                return table.KeepWhenEmpty ? bool.TrueString : bool.FalseString;
            }

            MetadataValue value = GetMetadata(table, field, propertyName);
            return value == null ? string.Empty : value.Value ?? string.Empty;
        }

        /// <summary>XMZADD 20260901 按白名单属性读取表或字段语义元数据，分类和布尔规则不伪装成文本元数据。</summary>
        private static MetadataValue GetMetadata(TableMetadata table, FieldMetadata field, string propertyName)
        {
            if (field == null)
            {
                if (propertyName == "ChineseName") return table.ChineseName;
                if (propertyName == "ModuleName") return table.ModuleName;
                if (propertyName == "EntityName") return table.EntityName;
                if (propertyName == "BusinessMeaning") return table.BusinessMeaning;
                if (propertyName == "Remark") return table.Remark;
                return null;
            }
            if (propertyName == "ChineseName") return field.ChineseName;
            if (propertyName == "EntityPropertyName") return field.EntityPropertyName;
            if (propertyName == "BusinessMeaning") return field.BusinessMeaning;
            if (propertyName == "Usage") return field.Usage;
            if (propertyName == "RelationSummary") return field.RelationSummary;
            if (propertyName == "Remark") return field.Remark;
            return null;
        }

        /// <summary>XMZADD 20260831 创建统一操作按钮，保持保存与取消动作在编辑窗口中清晰可见。</summary>
        private static Button CreateButton(string content, RoutedEventHandler handler)
        {
            var button = new Button { Content = content, MinWidth = 94D, Height = 30D, Margin = new Thickness(0D, 0D, 8D, 0D) };
            button.Click += handler;
            return button;
        }
    }
}
