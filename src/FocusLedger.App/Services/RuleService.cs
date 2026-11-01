using FocusLedger.Core;
using FocusLedger.Storage;

namespace FocusLedger.App.Services;

/// <summary>
/// 规则的唯一权威来源：内置规则（代码）+ 用户规则（数据库）+ 内置规则的启用状态。
/// 任何变更都会重建 <see cref="RuleEngine"/>，并通知界面刷新。
/// </summary>
public sealed class RuleService
{
    private readonly FocusLedgerStore _store;

    public RuleService(FocusLedgerStore store)
    {
        _store = store;
        Reload();
    }

    /// <summary>当前生效的规则引擎。报告与采集都应使用它。</summary>
    public RuleEngine Current { get; private set; } = RuleEngine.Default;

    /// <summary>当前实际生效的全部规则（已排除被停用的内置规则）。</summary>
    public IReadOnlyList<Rule> EffectiveRules { get; private set; } = Array.Empty<Rule>();

    public IReadOnlyList<Rule> UserRules { get; private set; } = Array.Empty<Rule>();

    public IReadOnlySet<int> DisabledBuiltin { get; private set; } = new HashSet<int>();

    public event EventHandler? Changed;

    public void Reload()
    {
        UserRules = _store.LoadUserRules();
        DisabledBuiltin = _store.LoadDisabledBuiltinIds();

        EffectiveRules = BuiltinRules.All
            .Where(r => !DisabledBuiltin.Contains(r.Id))
            .Concat(UserRules)
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Id)
            .ToList();

        Current = new RuleEngine(EffectiveRules);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetEnabled(Rule rule, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.IsBuiltin)
        {
            var disabled = DisabledBuiltin.ToHashSet();
            if (enabled)
            {
                disabled.Remove(rule.Id);
            }
            else
            {
                disabled.Add(rule.Id);
            }

            _store.SaveDisabledBuiltinIds(disabled);
        }
        else
        {
            _store.UpdateUserRule(rule with { Enabled = enabled });
        }

        Reload();
    }

    /// <summary>内置规则只能停用，不能改类别；要改就新建一条优先级更高的自定义规则。</summary>
    public void SetCategory(Rule rule, ContextLevel category)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.IsBuiltin)
        {
            return;
        }

        _store.UpdateUserRule(rule with { Context = category });
        Reload();
    }

    public Rule AddRule(
        RuleMatchType matchType,
        string pattern,
        ContextLevel category,
        AppClass appClass,
        int priority,
        string? label)
    {
        var created = _store.InsertUserRule(
            new Rule(0, matchType, pattern, category, appClass, priority, Enabled: true, IsBuiltin: false, label));

        Reload();
        return created;
    }

    public void Delete(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.IsBuiltin)
        {
            return;
        }

        _store.DeleteUserRule(rule.Id);
        Reload();
    }

    public Rule? FindProcessRule(string app) =>
        EffectiveRules.FirstOrDefault(
            r => r.MatchType == RuleMatchType.Process
                 && string.Equals(r.Pattern, app, StringComparison.OrdinalIgnoreCase));
}
