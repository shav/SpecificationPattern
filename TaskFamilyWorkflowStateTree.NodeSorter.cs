using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.Domain.Shared;
using Sungero.Metadata;
using Sungero.WebAPI.Models;
using Sungero.Workflow;
using Sungero.Workflow.Interfaces;

namespace Sungero.WebAPI.ApplicationServices.Workflow.TaskFamilyWorkflowState.Tree;

public partial class TaskFamilyWorkflowStateTree
{
  /// <summary>
  /// Сортировщик узлов дерева состояния семейства ЗЗУ.
  /// </summary>
  private class NodeSorter
  {
    #region Вложенные типы

    /// <summary>
    /// Базовый критерий сортировки по свойству узла дерева состояния.
    /// </summary>
    private abstract class NodePropertySortCriterionBase
    {
      /// <summary>
      /// Применить критерий сортировки к коллекции узлов.
      /// </summary>
      /// <param name="nodes">Узлы дерева состояния.</param>
      /// <param name="sortDirection">Направление сортировки.</param>
      /// <returns>Отсортированные узлы дерева состояния.</returns>
      public abstract IEnumerable<TaskFamilyWorkflowStateTreeNode> Apply(IEnumerable<TaskFamilyWorkflowStateTreeNode> nodes, SortDirection sortDirection);
    }

    /// <summary>
    /// Универсальный критерий сортировки по свойству узла дерева состояния.
    /// </summary>
    /// <typeparam name="T">Тип свойства.</typeparam>
    private class NodePropertySortCriterion<T> : NodePropertySortCriterionBase
    {
      /// <summary>
      /// Функция для получения значения свойства у конкретного узла дерева состояния.
      /// </summary>
      public Func<TaskFamilyWorkflowStateTreeNode, T> PropertyValueGetter { get; private set; }

      /// <summary>
      /// Компаратор значений свойства.
      /// </summary>
      protected virtual IComparer<T> Comparer { get; }

      public override IEnumerable<TaskFamilyWorkflowStateTreeNode> Apply(IEnumerable<TaskFamilyWorkflowStateTreeNode> nodes, SortDirection sortDirection)
      {
        var propertySortCriterion = (TaskFamilyWorkflowStateTreeNode node) => this.GetPropertySortValue(node);
        if (nodes is IOrderedEnumerable<TaskFamilyWorkflowStateTreeNode> sortedNodes)
        {
          return sortDirection == SortDirection.Descending
           ? sortedNodes.ThenByDescending(propertySortCriterion, this.Comparer)
           : sortedNodes.ThenBy(propertySortCriterion, this.Comparer);
        }
        else
        {
          return sortDirection == SortDirection.Descending
            ? nodes.OrderByDescending(propertySortCriterion, this.Comparer)
            : nodes.OrderBy(propertySortCriterion, this.Comparer);
        }
      }

      /// <summary>
      /// Получить для конкретного узла дерева состояния значение свойства, используемое для сортировки.
      /// </summary>
      /// <param name="node">Узел дерева состояния.</param>
      /// <returns>Значение свойства для сортировки.</returns>
      protected virtual T GetPropertySortValue(TaskFamilyWorkflowStateTreeNode node)
      {
        return this.PropertyValueGetter(node);
      }

      /// <summary>
      /// Конструктор.
      /// </summary>
      /// <param name="propertyValueGetter">Функция для получения значения свойства у конкретного узла дерева состояния.</param>
      /// <exception cref="ArgumentNullException">Когда propertyGetter не задан.</exception>
      public NodePropertySortCriterion(Func<TaskFamilyWorkflowStateTreeNode, T> propertyValueGetter)
      {
        if (propertyValueGetter == null)
          throw new ArgumentNullException(nameof(propertyValueGetter));

        this.PropertyValueGetter = propertyValueGetter;
      }
    }

    /// <summary>
    /// Критерий сортировки по строковому свойству узла дерева состояния.
    /// </summary>
    private class StringNodePropertySortCriterion : NodePropertySortCriterion<string>
    {
      protected override IComparer<string> Comparer => StringComparer.CurrentCultureIgnoreCase;

      /// <summary>
      /// Конструктор.
      /// </summary>
      /// <param name="propertyValueGetter">Функция для получения значения свойства у конкретного узла дерева состояния.</param>
      /// <exception cref="ArgumentNullException">Когда propertyGetter не задан.</exception>
      public StringNodePropertySortCriterion(Func<TaskFamilyWorkflowStateTreeNode, string> propertyValueGetter)
        : base(propertyValueGetter)
      {
      }
    }

    /// <summary>
    /// Критерий сортировки по свойству типа "Перечисление" узла дерева состояния.
    /// </summary>
    private class EnumNodePropertySortCriterion : NodePropertySortCriterion<string>
    {
      /// <summary>
      /// Название свойства-перечисления сущности.
      /// </summary>
      public string PropertyName { get; private set; }

      protected override IComparer<string> Comparer => StringComparer.CurrentCultureIgnoreCase;

      protected override string GetPropertySortValue(TaskFamilyWorkflowStateTreeNode node)
      {
        var rawPropertyValue = this.PropertyValueGetter(node);
        Enumeration? enumValue = string.IsNullOrWhiteSpace(rawPropertyValue) ? null : new Enumeration(rawPropertyValue);
        var entityMetadata = node.EntityType.GetEntityMetadata().GetFinal();
        var enumProperty = new EnumPropertyInfo((EnumPropertyMetadata)entityMetadata.GetProperty(this.PropertyName));
        return enumProperty?.GetLocalizedValue(enumValue) ?? string.Empty;
      }

      /// <summary>
      /// Конструктор.
      /// </summary>
      /// <param name="propertyValueGetter">Функция для получения значения свойства у конкретного узла дерева состояния.</param>
      /// <param name="propertyName">Название свойства-перечисления сущности.</param>
      /// <exception cref="ArgumentNullException">Когда propertyGetter или название свойства сущности не заданы.</exception>
      public EnumNodePropertySortCriterion(Func<TaskFamilyWorkflowStateTreeNode, string> propertyValueGetter, string propertyName)
        : base(propertyValueGetter)
      {
        if (string.IsNullOrWhiteSpace(propertyName))
          throw new ArgumentNullException(nameof(propertyName));

        this.PropertyName = propertyName;
      }
    }

    #endregion

    /// <summary>
    /// Маппинг свойств узла дерева состояния на критерии сортировки по соответствующему свойству.
    /// </summary>
    private static readonly Dictionary<string, IEnumerable<NodePropertySortCriterionBase>> nodePropertiesToSortCriteriaMap = new()
    {
      {
        nameof(TaskFamilyWorkflowStateTreeNode.NodeId),
        new NodePropertySortCriterionBase[] { new NodePropertySortCriterion<bool>(n => n.IsAssignment), new NodePropertySortCriterion<long>(n => n.Id), new NodePropertySortCriterion<int>(n => n.StartId) }
      },
      { nameof(TaskFamilyWorkflowStateTreeNode.UserName), new[] { new StringNodePropertySortCriterion(n => n.UserName) } },
      { nameof(TaskFamilyWorkflowStateTreeNode.Deadline), new[] { new NodePropertySortCriterion<DateTime?>(n => n.Deadline) } },
      { nameof(TaskFamilyWorkflowStateTreeNode.Status), new[] { new EnumNodePropertySortCriterion(n => n.Status, nameof(IWorkflowEntity.Status)) } },
      { nameof(TaskFamilyWorkflowStateTreeNode.Result), new[] { new EnumNodePropertySortCriterion(n => n.Result, nameof(IAssignment.Result)) } },
      // Даты отправки может не быть, если не стартованную задачу прекратили - в этом случае будет сортирвка по Completed.
      // Другой случай, задача-черновик - не имеет ни времени старта, ни времени завершения, но должна распологаться последней.
      { nameof(TaskFamilyWorkflowStateTreeNode.Sent), new[] { new NodePropertySortCriterion<DateTime?>(n => n.Sent ?? n.Completed ?? DateTime.MaxValue) } },
      { nameof(TaskFamilyWorkflowStateTreeNode.Completed), new[] { new NodePropertySortCriterion<DateTime?>(n => n.Completed) } },
      { nameof(TaskFamilyWorkflowStateTreeNode.Subject), new[] { new StringNodePropertySortCriterion(n => n.Subject) } }
    };

    /// <summary>
    /// Отсортировать указанные узлы дерева состояния.
    /// </summary>
    /// <param name="nodes">Узлы дерева состояния.</param>
    /// <param name="sortCriteria">Критерии сортировки.</param>
    /// <returns>Отсортированные узлы дерева состояния.</returns>
    public IEnumerable<TaskFamilyWorkflowStateTreeNode> SortNodes(IEnumerable<TaskFamilyWorkflowStateTreeNode> nodes, IEnumerable<SortCriterion> sortCriteria)
    {
      if (nodes == null || !nodes.Any())
        return nodes;

      var sortedNodes = nodes;
      foreach (var sortCriterion in sortCriteria)
      {
        var sortProperty = GetNodeProperty(sortCriterion);
        var nodeSortCriteria = sortProperty != null ? nodePropertiesToSortCriteriaMap.GetValueOrDefault(sortProperty) : null;
        if (nodeSortCriteria == null)
          continue;

        foreach (var nodeSort in nodeSortCriteria)
          sortedNodes = nodeSort.Apply(sortedNodes, sortCriterion.Direction);
      }

      return sortedNodes;
    }

    /// <summary>
    /// Получить свойство узла дерева состояния, по которому должна выполняться сортировка.
    /// </summary>
    /// <param name="sortCriterion">Критерий сортировки по колонке грида.</param>
    /// <returns>Свойство узла дерева состояния.</returns>
    private static string GetNodeProperty(SortCriterion sortCriterion)
    {
      return sortCriterion?.PropertyName switch
      {
        null => null,
        nameof(TaskFamilyWorkflowStateTreeNode.NodeId) => nameof(TaskFamilyWorkflowStateTreeNode.NodeId),
        _ => gridColumnsToNodePropertiesMap.GetValueOrDefault(sortCriterion.PropertyName)
      };
    }
  }
}
