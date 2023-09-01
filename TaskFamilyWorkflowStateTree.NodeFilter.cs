using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using CommonLibrary;
using Newtonsoft.Json.Linq;
using Sungero.Core;
using Sungero.CoreEntities.Shared;
using Sungero.Domain.Shared;
using Sungero.Metadata;
using Sungero.WebAPI.Models;
using Sungero.WebAPI.Services.ColumnFilters;
using Sungero.WebAPI.Services.ColumnFilters.WorkflowSubject;

namespace Sungero.WebAPI.ApplicationServices.Workflow.TaskFamilyWorkflowState.Tree;

public partial class TaskFamilyWorkflowStateTree
{
  #region Вложенные типы

  /// <summary>
  /// Интерфейс фильтра узлов дерева состония по конкретному свойству.
  /// </summary>
  private interface INodeFilter
  {
    /// <summary>
    /// Применить фильтр по свойству к списку узлов дерева состояния.
    /// </summary>
    /// <param name="nodes">Список узлов дерева состояния.</param>
    /// <returns>Отфильтрованный список узлов дерева состояния.</returns>
    IEnumerable<TaskFamilyWorkflowStateTreeNode> Apply(IEnumerable<TaskFamilyWorkflowStateTreeNode> nodes);
  }

  /// <summary>
  /// Парсер значений критериев фильтрации.
  /// </summary>
  private interface ICriterionValueParser
  {
    /// <summary>
    /// Распарсить из критерия фильтрации коллекцию значений, удовлетворяющих фильтру.
    /// </summary>
    /// <param name="propertyType">Тип свойства.</param>
    /// <param name="criterion">Критерий фильтрации.</param>
    /// <returns>Набор значений, подпадающих под фильтр.</returns>
    IEnumerable<object> ParseCriterionValues(PropertyType propertyType, ExtendedSearchCriterionValue criterion);

    /// <summary>
    /// Извлечь из коллекции значений критерия фильтрации набор подпадающих под фильтр значений указанного типа.
    /// </summary>
    /// <typeparam name="TValue">Тип значений.</typeparam>
    /// <param name="criterionValues">"Сырые" значения критерия фильтрации.</param>
    /// <returns>Набор значений указанного типа, подпадающих под фильтр.</returns>
    IEnumerable<TValue> ExtractValueList<TValue>(IEnumerable<object> criterionValues);
  }

  /// <summary>
  /// Комбинированный фильтр узлов дерева состояния.
  /// </summary>
  private class CompositeNodeFilter : INodeFilter
  {
    /// <summary>
    /// Фильтры.
    /// </summary>
    private readonly IEnumerable<INodeFilter> filters;

    #region INodeFilter

    public IEnumerable<TaskFamilyWorkflowStateTreeNode> Apply(IEnumerable<TaskFamilyWorkflowStateTreeNode> nodes)
    {
      if (!this.filters.Any())
        return nodes;

      return this.filters.Aggregate(nodes, (current, filter) => filter.Apply(current));
    }

    #endregion

    /// <summary>
    /// Создать экземпляр композитного фильтра.
    /// </summary>
    /// <param name="filters">Фильтры, которые требуется применить.</param>
    /// <exception cref="ArgumentNullException">Если переданный аргумент filters будет null.</exception>
    public CompositeNodeFilter(IEnumerable<INodeFilter> filters)
    {
      this.filters = filters ?? throw new ArgumentNullException(nameof(filters));
    }
  }

  /// <summary>
  /// Специализированный фильтр узлов дерева состония по конкретному свойству.
  /// </summary>
  /// <typeparam name="T">Тип свойства.</typeparam>
  private abstract class NodeFilter<T> : INodeFilter
  {
    /// <summary>
    /// Критерий фильтрации.
    /// </summary>
    public ExtendedSearchCriterionValue Criterion { get; }

    /// <summary>
    /// Набор фильтруемых значений критерия фильтрации.
    /// </summary>
    public IEnumerable<object> CriterionValues { get; }

    /// <summary>
    /// Селектор свойства узла дерева состояния, по которому выполняется фильтрация.
    /// </summary>
    public Func<TaskFamilyWorkflowStateTreeNode, T> PropertyValueGetter { get; }

    /// <summary>
    /// Признак того, что в списке значений критерия фильтрации есть пустое значение.
    /// </summary>
    protected bool HasEmptyCriterionValue { get; }

    /// <summary>
    /// Компаратор значений свойства.
    /// </summary>
    protected virtual IEqualityComparer<T> Comparer => EqualityComparer<T>.Default;

    /// <summary>
    /// Парсер значений критерия фильтрации.
    /// </summary>
    protected ICriterionValueParser Parser { get; }

    /// <summary>
    /// Парсер по-умолчанию для значений критерия фильтрации.
    /// </summary>
    private static readonly ICriterionValueParser defaultParser = new NodePropertyCriterionValueParser();

    /// <summary>
    /// Проверить, удовлетворяет ли значение свойства узла дерева состояния значению фильтра.
    /// </summary>
    /// <param name="value">Значение свойства узла дерева состояния.</param>
    /// <param name="filterValue">Значение фильтра.</param>
    /// <returns>True - если значение свойства узла дерева состояния удовлетворяет значению фильтра, False - иначе.</returns>
    protected virtual bool IsMatchToFilterValue(T value, T filterValue)
    {
      return this.Comparer.Equals(value, filterValue);
    }

    /// <summary>
    /// Проверить, что значение свойства является пустым.
    /// </summary>
    /// <param name="value">Значение свойства.</param>
    /// <returns>True - если значение свойства является пустым, False - иначе.</returns>
    protected virtual bool IsEmptyValue(T value)
    {
      return value == null;
    }

    #region INodeFilter

    public abstract IEnumerable<TaskFamilyWorkflowStateTreeNode> Apply(IEnumerable<TaskFamilyWorkflowStateTreeNode> nodes);

    #endregion

    /// <summary>
    /// Конструктор.
    /// </summary>
    /// <param name="propertyValueGetter">Селектор свойства узла дерева состояния, по которому выполняется фильтрация.</param>
    /// <param name="propertyType">Тип свойства.</param>
    /// <param name="criterion">Критерий фильтрации.</param>
    /// <param name="parser">Парсер значений критерия фильтрации.</param>
    /// <exception cref="ArgumentNullException">Когда не задан селектор свойства узла или критерий фильтрации.</exception>
    protected NodeFilter(Func<TaskFamilyWorkflowStateTreeNode, T> propertyValueGetter, PropertyType propertyType, ExtendedSearchCriterionValue criterion, ICriterionValueParser parser = null)
    {
      if (propertyValueGetter == null)
        throw new ArgumentNullException(nameof(propertyValueGetter));

      if (criterion == null)
        throw new ArgumentNullException(nameof(criterion));

      this.Parser = parser ?? defaultParser;
      this.PropertyValueGetter = propertyValueGetter;
      this.Criterion = criterion;
      this.CriterionValues = this.Parser.ParseCriterionValues(propertyType, criterion);

      // Случай, когда в списке значений есть NotNull без галочки для исключения значений, является нереалистичным.
      this.HasEmptyCriterionValue = this.CriterionValues?.Contains(default(NullSearchCriteriaComparand)) == true ||
        (this.CriterionValues?.Contains(default(NotNullSearchCriteriaComparand)) == true && this.Criterion.IsExcludeFilter);
    }
  }

  /// <summary>
  /// Базовый фильтр узлов дерева состояния по вхождению значения свойства в список выбранных значений.
  /// </summary>
  private abstract class ValueListNodeFilter<T> : NodeFilter<T>
  {
    /// <summary>
    /// Список значений свойства, подпадающих под фильтр.
    /// </summary>
    protected abstract IEnumerable<T> ValueList { get; }

    /// <summary>
    /// Проверить, содержит ли список фильтруемых значений свойства указанное значение.
    /// </summary>
    /// <param name="value">Значение свойства узла дерева состояния.</param>
    /// <returns>True - если указанное значение входит в список фильтруемых значений, False - иначе.</returns>
    private bool CriterionValuesContains(T value)
    {
      return (this.HasEmptyCriterionValue && this.IsEmptyValue(value)) || this.ValueList.Any(filterValue => this.IsMatchToFilterValue(value, filterValue));
    }

    public override IEnumerable<TaskFamilyWorkflowStateTreeNode> Apply(IEnumerable<TaskFamilyWorkflowStateTreeNode> nodes)
    {
      if (this.Criterion.IsExcludeFilter)
        return nodes.Where(n => !this.CriterionValuesContains(value: this.PropertyValueGetter(n)));
      else
        return nodes.Where(n => this.CriterionValuesContains(value: this.PropertyValueGetter(n)));
    }

    /// <summary>
    /// Конструктор.
    /// </summary>
    /// <param name="propertyValueGetter">Селектор свойства узла дерева состояния, по которому выполняется фильтрация.</param>
    /// <param name="propertyType">Тип свойства.</param>
    /// <param name="criterion">Критерий фильтрации.</param>
    protected ValueListNodeFilter(Func<TaskFamilyWorkflowStateTreeNode, T> propertyValueGetter, PropertyType propertyType, ExtendedSearchCriterionValue criterion)
      : base(propertyValueGetter, propertyType, criterion)
    {
    }
  }

  /// <summary>
  /// Фильтр узлов дерева состояния по свойству навигации.
  /// </summary>
  private class NavigationNodeFilter : ValueListNodeFilter<long>
  {
    /// <summary>
    /// Идентификаторы сущностей, подпадающих под фильтр.
    /// </summary>
    private readonly IEnumerable<long> entityIds;

    protected override IEnumerable<long> ValueList => this.entityIds;

    /// <summary>
    /// Конструктор.
    /// </summary>
    /// <param name="propertyValueGetter">Селектор свойства узла дерева состояния, по которому выполняется фильтрация.</param>
    /// <param name="criterion">Критерий фильтрации.</param>
    public NavigationNodeFilter(Func<TaskFamilyWorkflowStateTreeNode, long> propertyValueGetter, ExtendedSearchCriterionValue criterion)
      : base(propertyValueGetter, PropertyType.Navigation, criterion)
    {
      this.entityIds = this.Parser.ExtractValueList<long>(this.CriterionValues);
    }
  }

  /// <summary>
  /// Фильтр узлов дерева состояния по свойству типа "Перечисление".
  /// </summary>
  private class EnumNodeFilter : ValueListNodeFilter<string>
  {
    /// <summary>
    /// Значения перечисления, подпадающие под фильтр.
    /// </summary>
    private readonly IEnumerable<string> enumValues;

    protected override IEnumerable<string> ValueList => this.enumValues;

    protected override bool IsEmptyValue(string value)
    {
      return string.IsNullOrEmpty(value);
    }

    /// <summary>
    /// Конструктор.
    /// </summary>
    /// <param name="propertyValueGetter">Селектор свойства узла дерева состояния, по которому выполняется фильтрация.</param>
    /// <param name="criterion">Критерий фильтрации.</param>
    public EnumNodeFilter(Func<TaskFamilyWorkflowStateTreeNode, string> propertyValueGetter, ExtendedSearchCriterionValue criterion)
      : base(propertyValueGetter, PropertyType.Enumeration, criterion)
    {
      this.enumValues = this.Parser.ExtractValueList<Enumeration>(this.CriterionValues).Select(e => e.Value).ToArray();
    }
  }

  /// <summary>
  /// Фильтр узлов дерева состояния по свойству строкового типа.
  /// </summary>
  private class StringNodeFilter : ValueListNodeFilter<string>
  {
    /// <summary>
    /// Строковые значения, подпадающие под фильтр.
    /// </summary>
    private readonly IEnumerable<string> stringValues;

    protected override IEnumerable<string> ValueList => this.stringValues;

    protected override IEqualityComparer<string> Comparer => StringComparer.CurrentCultureIgnoreCase;

    protected override bool IsMatchToFilterValue(string propertyValue, string filterValue)
    {
      return propertyValue?.Contains(filterValue, StringComparison.CurrentCultureIgnoreCase) == true;
    }

    protected override bool IsEmptyValue(string value)
    {
      return string.IsNullOrEmpty(value);
    }

    /// <summary>
    /// Конструктор.
    /// </summary>
    /// <param name="propertyValueGetter">Селектор свойства узла дерева состояния, по которому выполняется фильтрация.</param>
    /// <param name="criterion">Критерий фильтрации.</param>
    public StringNodeFilter(Func<TaskFamilyWorkflowStateTreeNode, string> propertyValueGetter, ExtendedSearchCriterionValue criterion)
      : base(propertyValueGetter, PropertyType.String, criterion)
    {
      this.stringValues = this.Parser.ExtractValueList<string>(this.CriterionValues);
    }
  }

  /// <summary>
  /// Фильтр узлов дерева состояния по свойству типа "Дата со временем".
  /// </summary>
  private class DateTimeNodeFilter : NodeFilter<DateTime?>
  {
    /// <summary>
    /// Значение критерия фильтрации:
    /// * либо временной интервал с явно заданными границами
    /// * либо относительный временной интервал
    /// * либо точная дата.
    /// </summary>
    private ExtendedDateRelativeValueArgs dateTimeCriterionValue;

    /// <summary>
    /// Парсер значений критериев фильтрации по колонке типа "Дата со временем".
    /// </summary>
    private static readonly ICriterionValueParser dateTimeCriterionValueParser = new DateTimeNodePropertyCriterionValueParser();

    /// <summary>
    /// Получить выражение фильтрации для поиска по свойству типа "Дата со временем".
    /// </summary>
    /// <param name="dateTimeProperty">Свойство узлов дерева состояния, по которому выполняется фильтрация.</param>
    /// <returns>Выражение фильтрации.</returns>
    private Func<TaskFamilyWorkflowStateTreeNode, bool> GetFilterByDateTime(System.Reflection.PropertyInfo dateTimeProperty)
    {
      Func<TaskFamilyWorkflowStateTreeNode, bool> filterByDateTime = null;
      if (this.dateTimeCriterionValue != null)
      {
        Expression filterExpression;
        var typeParameter = Expression.Parameter(typeof(TaskFamilyWorkflowStateTreeNode));
        var propertyExpression = Expression.MakeMemberAccess(typeParameter, dateTimeProperty);

        var operation = (DateTimeOperation)this.Criterion.Operation;
        if (operation == DateTimeOperation.ExactDateTime || operation == DateTimeOperation.ExcludeExactDateTime)
        {
          var dateTime = ExtendedDateManager.ConvertToDateTime(operation, this.dateTimeCriterionValue, out var tenantUtcOffset, out var clientUtcOffset);
          filterExpression = FilterExpressionBuilder.ExactDateTimeFilterExpression(propertyExpression, dateTime, tenantUtcOffset, clientUtcOffset);
        }
        else
        {
          var dateTimeValues = ExtendedDateManager.ConvertToDateRange(operation, this.dateTimeCriterionValue, out var tenantUtcOffset, out var clientUtcOffset);
          filterExpression = FilterExpressionBuilder.ExtendedDateFilterExpression(operation, propertyExpression, dateTimeValues, tenantUtcOffset, clientUtcOffset);
        }

        filterByDateTime = filterExpression != null
          ? Expression.Lambda<Func<TaskFamilyWorkflowStateTreeNode, bool>>(filterExpression, typeParameter).Compile()
          : null;
      }

      if (this.HasEmptyCriterionValue)
      {
        if (this.Criterion.IsExcludeFilter)
          return node => !this.IsEmptyValue(this.PropertyValueGetter(node)) && filterByDateTime?.Invoke(node) != false;
        else
          return node => this.IsEmptyValue(this.PropertyValueGetter(node)) || filterByDateTime?.Invoke(node) == true;
      }

      return filterByDateTime;
    }

    public override IEnumerable<TaskFamilyWorkflowStateTreeNode> Apply(IEnumerable<TaskFamilyWorkflowStateTreeNode> nodes)
    {
      var propertyName = GetNodeProperty(this.Criterion) ??
        throw new ArgumentException($"Property for criterion with name \"{this.Criterion.CriterionName}\" does not exist at {nameof(TaskFamilyWorkflowStateTreeNode)}");

      var dateTimeProperty = typeof(TaskFamilyWorkflowStateTreeNode).GetProperty(propertyName) ??
        throw new ArgumentException($"Property with name \"{propertyName}\" does not exist at {nameof(TaskFamilyWorkflowStateTreeNode)}");

      var filterByDateTime = this.GetFilterByDateTime(dateTimeProperty);
      return filterByDateTime != null ? nodes.Where(filterByDateTime) : nodes;
    }

    /// <summary>
    /// Конструктор.
    /// </summary>
    /// <param name="propertyValueGetter">Селектор свойства узла дерева состояния, по которому выполняется фильтрация.</param>
    /// <param name="criterion">Критерий фильтрации.</param>
    public DateTimeNodeFilter(Func<TaskFamilyWorkflowStateTreeNode, DateTime?> propertyValueGetter, ExtendedSearchCriterionValue criterion)
      : base(propertyValueGetter, PropertyType.DateTime, criterion, dateTimeCriterionValueParser)
    {
      // С клиента приходит только один из возможных видов критериев по дате - либо временной интервал, либо точная дата.
      this.dateTimeCriterionValue = this.Parser.ExtractValueList<ExtendedDateRelativeValueArgs>(this.CriterionValues).SingleOrDefault();
    }
  }

  /// <summary>
  /// Комбинированный фильтр узлов дерева состояния по теме и важности.
  /// </summary>
  private class SubjectAndImportanceNodeFilter : CompositeNodeFilter
  {
    /// <summary>
    /// Значение высокой важности.
    /// </summary>
    private const string HighImportanceValue = nameof(Sungero.Workflow.Task.Importance.High);

    /// <summary>
    /// Создать фильтр по колонке "Тема".
    /// </summary>
    /// <param name="subjectCriterionValue">Значение фильтра "Тема и Важность".</param>
    /// <param name="isExcludeFilter">Признак того, что операция фильтрации исключающая.</param>
    /// <param name="operation">Операция фильтра.</param>
    /// <returns>Фильтр по колонке "Тема".</returns>
    private static ExtendedSearchCriterionValue CreateSubjectPropertyFilter(SubjectCriterionValue subjectCriterionValue, bool isExcludeFilter, int operation)
    {
      return new ExtendedSearchCriterionValue()
      {
        CriterionName = WorkflowEntityPropertyNames.Subject,
        IsExcludeFilter = isExcludeFilter,
        Operation = operation,
        Value = subjectCriterionValue.Subject
      };
    }

    /// <summary>
    /// Создать фильтр по колонке "Важность".
    /// </summary>
    /// <param name="subjectCriterionValue">Значение фильтра "Тема и Важность".</param>
    /// <param name="isExcludeFilter">Признак того, что операция фильтрации исключающая.</param>
    /// <returns>Фильтр по колонке "Важность".</returns>
    private static ExtendedSearchCriterionValue CreateImportantPropertyFilter(SubjectCriterionValue subjectCriterionValue, bool isExcludeFilter)
    {
      return new ExtendedSearchCriterionValue()
      {
        CriterionName = WorkflowEntityPropertyNames.Importance,
        IsExcludeFilter = isExcludeFilter,
        Operation = (int)(isExcludeFilter ? EnumOperation.Except : subjectCriterionValue.Important ? EnumOperation.OneOf : EnumOperation.All),
        Value = subjectCriterionValue.Important ? new JArray(HighImportanceValue) : null
      };
    }

    /// <summary>
    /// Конструктор.
    /// </summary>
    /// <param name="criterion">Критерий фильтрации.</param>
    public static SubjectAndImportanceNodeFilter Create(ExtendedSearchCriterionValue criterion)
    {
      var filters = new List<INodeFilter>();
      if (SubjectCriterionValue.TryParse(criterion, out var subjectCriterionValue))
      {
        if (subjectCriterionValue.Subject != null)
        {
          var subjectPropertyCriterion = CreateSubjectPropertyFilter(subjectCriterionValue, criterion.IsExcludeFilter, criterion.Operation);
          filters.Add(new StringNodeFilter(n => n.Subject, subjectPropertyCriterion));
        }

        if (subjectCriterionValue.Important)
          filters.Add(new EnumNodeFilter(n => n.Importance, CreateImportantPropertyFilter(subjectCriterionValue, criterion.IsExcludeFilter)));
      }

      return new SubjectAndImportanceNodeFilter(filters);
    }

    /// <summary>
    /// Конструктор.
    /// </summary>
    /// <param name="filters">Фильтры, которые требуется объединить.</param>
    public SubjectAndImportanceNodeFilter(IEnumerable<INodeFilter> filters)
      : base(filters)
    {
    }
  }

  /// <summary>
  /// Универсальнй парсер значений критериев фильтрации.
  /// </summary>
  private class NodePropertyCriterionValueParser : ICriterionValueParser
  {
    #region ICriterionValueParser

    public virtual IEnumerable<object> ParseCriterionValues(PropertyType propertyType, ExtendedSearchCriterionValue criterion)
    {
      var parsedCriterionValue = CriterionValueParser.ParseCriterionValue(propertyType, criterion);
      return parsedCriterionValue is IEnumerable<object> valueList ? valueList : new[] { parsedCriterionValue };
    }

    public IEnumerable<TValue> ExtractValueList<TValue>(IEnumerable<object> criterionValue)
    {
      return (criterionValue?.OfType<IEnumerable<TValue>>()?.SelectMany(_ => _) ?? Enumerable.Empty<TValue>())
        .Union(criterionValue?.OfType<TValue>() ?? Enumerable.Empty<TValue>())
        .ToArray();
    }

    #endregion
  }

  /// <summary>
  /// Парсер значений критериев фильтрации по колонке типа "Дата со временем".
  /// </summary>
  private class DateTimeNodePropertyCriterionValueParser : NodePropertyCriterionValueParser
  {
    public override IEnumerable<object> ParseCriterionValues(PropertyType propertyType, ExtendedSearchCriterionValue criterion)
    {
      var parsedCriterionValues = base.ParseCriterionValues(propertyType, criterion);
      var criterionValues = parsedCriterionValues
        .Where(value => (value is NullSearchCriteriaComparand) || (value is NotNullSearchCriteriaComparand) ||
                        (value is DateRangeValue) || (value is DateTime))
        .Select(value => DateTimeValueProcessor.CreateArgs(value));

      var relativeDateTimeValues = this.ExtractValueList<Enumeration>(parsedCriterionValues);
      if (relativeDateTimeValues.Any())
        criterionValues = criterionValues.Append(DateTimeValueProcessor.CreateArgs(relativeDateTimeValues));

      return criterionValues.ToArray();
    }
  }

  #endregion

  #region Методы

  /// <summary>
  /// Создать специализированный фильтр узлов дерева состояния по конкретному свойству.
  /// </summary>
  /// <param name="criterion">Критерий фильтрации.</param>
  /// <returns>Специализированный фильтр по свойству узла дерева состояния.</returns>
  private static INodeFilter CreateNodeFilter(ExtendedSearchCriterionValue criterion)
  {
    var nodeProperty = GetNodeProperty(criterion);
    return nodeProperty switch
    {
      WorkflowEntityPropertyNames.SubjectAndImportance => SubjectAndImportanceNodeFilter.Create(criterion),
      nameof(TaskFamilyWorkflowStateTreeNode.UserName) => new NavigationNodeFilter(n => n.UserId, criterion),
      nameof(TaskFamilyWorkflowStateTreeNode.Deadline) => new DateTimeNodeFilter(n => n.Deadline, criterion),
      nameof(TaskFamilyWorkflowStateTreeNode.Status) => new EnumNodeFilter(n => n.Status, criterion),
      nameof(TaskFamilyWorkflowStateTreeNode.Result) => new EnumNodeFilter(n => n.Result, criterion),
      nameof(TaskFamilyWorkflowStateTreeNode.Sent) => new DateTimeNodeFilter(n => n.Sent, criterion),
      nameof(TaskFamilyWorkflowStateTreeNode.Completed) => new DateTimeNodeFilter(n => n.Completed, criterion),
      nameof(TaskFamilyWorkflowStateTreeNode.Importance) => new EnumNodeFilter(n => n.Importance, criterion),
      nameof(TaskFamilyWorkflowStateTreeNode.Subject) => new StringNodeFilter(n => n.Subject, criterion),
      _ => throw new ArgumentException($"Property for criterion with name \"{criterion.CriterionName}\" does not exist at {nameof(TaskFamilyWorkflowStateTreeNode)}")
    };
  }

  /// <summary>
  /// Получить свойство узла дерева состояния, по которому должна выполняться фильтрация.
  /// </summary>
  /// <param name="criterion">Критерий фильтрации по колонке грида.</param>
  /// <returns>Свойство узла дерева состояния.</returns>
  private static string GetNodeProperty(ExtendedSearchCriterionValue criterion)
  {
    return criterion?.CriterionName switch
    {
      null => null,
      WorkflowEntityPropertyNames.SubjectAndImportance => WorkflowEntityPropertyNames.SubjectAndImportance,
      _ => gridColumnsToNodePropertiesMap.GetValueOrDefault(criterion.CriterionName)
    };
  }

  #endregion
}
