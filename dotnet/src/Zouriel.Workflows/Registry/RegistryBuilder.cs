using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using Zouriel.Workflows.Json;

namespace Zouriel.Workflows.Registry;

/// <summary>Builds a <see cref="WorkflowRegistry"/> from CLR types with a fluent API.</summary>
public sealed class RegistryBuilder
{
    private readonly List<Func<RegistryBuilder, EntityDescriptor>> _entities = [];
    private readonly List<ActionDescriptor> _actions = [];
    private readonly List<SignalDescriptor> _signals = [];
    private readonly List<LabelledItem> _channels = [];
    private readonly List<LabelledItem> _roles = [];
    private readonly List<LabelledItem> _groups = [];
    internal readonly Dictionary<Type, string> EntityKeys = [];

    /// <summary>Registers an entity type. Only fields added in <paramref name="configure"/> are visible to workflows.</summary>
    public RegistryBuilder Entity<T>(string key, Action<EntityBuilder<T>> configure) where T : class
    {
        EntityKeys[typeof(T)] = key;
        _entities.Add(b =>
        {
            var eb = new EntityBuilder<T>(key, b);
            configure(eb);
            return eb.Build();
        });
        return this;
    }

    /// <summary>Registers an entity described by hand (no CLR binding), e.g. for a custom gateway.</summary>
    public RegistryBuilder Entity(EntityDescriptor descriptor)
    {
        _entities.Add(_ => descriptor);
        return this;
    }

    public RegistryBuilder Action<THandler>(string key, Action<ActionBuilder>? configure = null) where THandler : class, IWorkflowAction
    {
        var ab = new ActionBuilder(key);
        configure?.Invoke(ab);
        _actions.Add(ab.Build(typeof(THandler)));
        return this;
    }

    public RegistryBuilder Signal(string key, Action<SignalBuilder>? configure = null)
    {
        var sb = new SignalBuilder(key);
        configure?.Invoke(sb);
        _signals.Add(sb.Build());
        return this;
    }

    public RegistryBuilder Channel(string key, string label)
    {
        _channels.Add(new LabelledItem { Key = key, Label = label });
        return this;
    }

    public RegistryBuilder Role(string key, string label)
    {
        _roles.Add(new LabelledItem { Key = key, Label = label });
        return this;
    }

    public RegistryBuilder Group(string key, string label)
    {
        _groups.Add(new LabelledItem { Key = key, Label = label });
        return this;
    }

    public WorkflowRegistry Build()
    {
        var entities = _entities.Select(f => f(this)).ToList();
        var dup = entities.GroupBy(e => e.Key).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new InvalidOperationException($"Entity '{dup.Key}' is registered twice.");
        return new WorkflowRegistry
        {
            Entities = entities,
            Actions = [.. _actions],
            Signals = [.. _signals],
            Channels = _channels.Count > 0 ? [.. _channels] : [new LabelledItem { Key = "email", Label = "Email" }, new LabelledItem { Key = "inApp", Label = "In-app" }],
            Roles = [.. _roles],
            Groups = [.. _groups],
        };
    }

    internal static string Humanize(string name)
    {
        var spaced = Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", " $1");
        spaced = spaced.Replace('_', ' ').Trim();
        return spaced.Length == 0 ? name : char.ToUpperInvariant(spaced[0]) + spaced[1..].ToLowerInvariant();
    }

    internal static string CamelKey(string path)
    {
        var parts = path.Split('.');
        var s = string.Concat(parts.Select((p, i) => i == 0 ? char.ToLowerInvariant(p[0]) + p[1..] : char.ToUpperInvariant(p[0]) + p[1..]));
        return s;
    }
}

public sealed class EntityBuilder<T> where T : class
{
    private readonly string _key;
    private readonly RegistryBuilder _root;
    private readonly List<FieldDescriptor> _fields = [];
    private string? _label;
    private string? _plural;
    private string? _title;
    private Func<object, object?>? _id;

    internal EntityBuilder(string key, RegistryBuilder root)
    {
        _key = key;
        _root = root;
    }

    public EntityBuilder<T> Label(string label, string? plural = null)
    {
        _label = label;
        _plural = plural;
        return this;
    }

    /// <summary>The property holding the record's id. Defaults to <c>Id</c>.</summary>
    public EntityBuilder<T> Key<TKey>(Expression<Func<T, TKey>> id)
    {
        var f = id.Compile();
        _id = o => f((T)o);
        return this;
    }

    /// <summary>The field shown as the record's name in task lists and audit.</summary>
    public EntityBuilder<T> Title<TProp>(Expression<Func<T, TProp>> field)
    {
        _title = RegistryBuilder.CamelKey(MemberPath(field.Body));
        return this;
    }

    /// <summary>Adds a scalar field. Its workflow type is inferred from the CLR type unless set.</summary>
    public EntityBuilder<T> Field<TProp>(Expression<Func<T, TProp>> member, Action<FieldBuilder>? configure = null)
    {
        var path = MemberPath(member.Body);
        var fb = new FieldBuilder(RegistryBuilder.CamelKey(path), RegistryBuilder.Humanize(path.Split('.').Last()), InferType(typeof(TProp)));
        var enumType = Nullable.GetUnderlyingType(typeof(TProp)) ?? typeof(TProp);
        if (enumType.IsEnum) fb.Enum(enumType);
        configure?.Invoke(fb);
        var getter = member.Compile();
        var setter = BuildSetter(member);
        _fields.Add(fb.Build(o => Values.Normalize(SafeGet(getter, (T)o)), setter, path));
        return this;
    }

    /// <summary>Adds a reference to one related record of another registered entity.</summary>
    public EntityBuilder<T> Relation<TOther>(Expression<Func<T, TOther?>> navigation, string key, Action<FieldBuilder>? configure = null) where TOther : class
    {
        var path = MemberPath(navigation.Body);
        var fb = new FieldBuilder(key, RegistryBuilder.Humanize(path.Split('.').Last()), FieldTypes.Relation);
        configure?.Invoke(fb);
        var getter = navigation.Compile();
        _fields.Add(fb.Build(o => SafeGet(getter, (T)o), null, path, EntityKeyOf(typeof(TOther))));
        return this;
    }

    /// <summary>Adds a collection of related records (for <c>forEach</c>).</summary>
    public EntityBuilder<T> Collection<TOther>(Expression<Func<T, IEnumerable<TOther>>> navigation, string key, Action<FieldBuilder>? configure = null) where TOther : class
    {
        var path = MemberPath(navigation.Body);
        var fb = new FieldBuilder(key, RegistryBuilder.Humanize(path.Split('.').Last()), FieldTypes.Collection);
        configure?.Invoke(fb);
        var getter = navigation.Compile();
        _fields.Add(fb.Build(o => getter((T)o), null, path, EntityKeyOf(typeof(TOther))));
        return this;
    }

    // Entity builders run inside RegistryBuilder.Build(), after every Entity<T>() call has
    // recorded its key, so related entity keys are known here.
    /// <summary>Nested paths (<c>x => x.Programme.Name</c>) read as null when a relation on the way is empty.</summary>
    private static TProp? SafeGet<TProp>(Func<T, TProp> getter, T o)
    {
        try
        {
            return getter(o);
        }
        catch (NullReferenceException)
        {
            return default;
        }
    }

    private string EntityKeyOf(Type t) =>
        _root.EntityKeys.TryGetValue(t, out var k) ? k : throw new InvalidOperationException($"{typeof(T).Name} refers to {t.Name}, which is not a registered workflow entity.");

    internal EntityDescriptor Build()
    {
        var idProp = typeof(T).GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        var id = _id ?? (idProp is null ? null : o => idProp.GetValue(o));
        if (id is null) throw new InvalidOperationException($"{typeof(T).Name} has no Id property; call .Key(x => x.YourKey).");
        var dup = _fields.GroupBy(f => f.Key).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new InvalidOperationException($"Field '{dup.Key}' is registered twice on '{_key}'.");
        return new EntityDescriptor
        {
            Key = _key,
            Label = _label ?? RegistryBuilder.Humanize(typeof(T).Name),
            PluralLabel = _plural,
            TitleField = _title,
            Fields = [.. _fields],
            ClrType = typeof(T),
            IdGetter = o => Values.Normalize(id(o)),
        };
    }

    private static string InferType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t.IsEnum) return FieldTypes.Enum;
        if (t == typeof(string) || t == typeof(Guid)) return FieldTypes.Text;
        if (t == typeof(bool)) return FieldTypes.Boolean;
        if (t == typeof(DateOnly)) return FieldTypes.Date;
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return FieldTypes.DateTime;
        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(double) || t == typeof(float) || t == typeof(decimal)) return FieldTypes.Number;
        return FieldTypes.Json;
    }

    internal static string MemberPath(Expression e)
    {
        var parts = new Stack<string>();
        while (true)
        {
            switch (e)
            {
                case MemberExpression m:
                    parts.Push(m.Member.Name);
                    e = m.Expression!;
                    continue;
                case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u:
                    e = u.Operand;
                    continue;
                case ParameterExpression:
                    if (parts.Count == 0) throw new ArgumentException("Point at a property, e.g. x => x.Status.");
                    return string.Join('.', parts);
                default:
                    throw new ArgumentException($"Only property paths are allowed in workflow registrations, not '{e}'.");
            }
        }
    }

    private static Action<object, object?>? BuildSetter<TProp>(Expression<Func<T, TProp>> member)
    {
        if (member.Body is not MemberExpression { Member: PropertyInfo prop } m || !prop.CanWrite || prop.SetMethod?.IsPublic != true) return null;
        if (m.Expression is not ParameterExpression) return null; // nested paths are read-only
        var target = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
        var nullable = !prop.PropertyType.IsValueType || Nullable.GetUnderlyingType(prop.PropertyType) is not null;
        return (o, v) => prop.SetValue(o, ConvertTo(v, target, nullable, prop.Name));
    }

    internal static object? ConvertTo(object? v, Type target, bool nullable, string name)
    {
        v = Values.Normalize(v);
        if (v is Dictionary<string, object?> choice && choice.TryGetValue("value", out var cv)) v = cv;
        if (v is null || (v is string s0 && s0.Length == 0 && target != typeof(string)))
        {
            if (nullable) return null;
            throw new WorkflowException("invalidValue", $"{name} cannot be empty.");
        }
        try
        {
            if (target == typeof(string)) return Values.Text(v);
            if (target.IsEnum)
            {
                var text = Values.Text(v);
                if (Enum.TryParse(target, text, ignoreCase: true, out var e) && Enum.IsDefined(target, e!)) return e;
                throw new WorkflowException("invalidValue", $"'{text}' is not a valid {name}.");
            }
            if (target == typeof(bool)) return v is bool b ? b : bool.Parse(Values.Text(v));
            if (target == typeof(Guid)) return Guid.Parse(Values.Text(v));
            if (target == typeof(DateOnly)) return DateOnly.ParseExact(Values.Text(v)[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (target == typeof(DateTime)) return DateTime.Parse(Values.Text(v), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            if (target == typeof(DateTimeOffset)) return DateTimeOffset.Parse(Values.Text(v), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            var d = v is double dv ? dv : double.Parse(Values.Text(v), CultureInfo.InvariantCulture);
            if (target == typeof(decimal)) return (decimal)d;
            return Convert.ChangeType(d, target, CultureInfo.InvariantCulture);
        }
        catch (WorkflowException)
        {
            throw;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw new WorkflowException("invalidValue", $"'{Values.Text(v)}' is not a valid value for {name}.");
        }
    }
}

public sealed class FieldBuilder
{
    private string _key;
    private string _label;
    private string _type;
    private List<FieldOption>? _options;
    private bool? _watchable, _settable, _lifecycle, _sensitive, _readable, _required;

    internal FieldBuilder(string key, string label, string type)
    {
        _key = key;
        _label = label;
        _type = type;
    }

    public FieldBuilder Key(string key) { _key = key; return this; }
    public FieldBuilder Label(string label) { _label = label; return this; }
    public FieldBuilder Type(string type) { _type = type; return this; }
    public FieldBuilder Text() => Type(FieldTypes.Text);
    public FieldBuilder LongText() => Type(FieldTypes.LongText);
    public FieldBuilder Email() => Type(FieldTypes.Email);
    public FieldBuilder Phone() => Type(FieldTypes.Phone);
    public FieldBuilder Url() => Type(FieldTypes.Url);
    public FieldBuilder Number() => Type(FieldTypes.Number);
    public FieldBuilder Currency() => Type(FieldTypes.Currency);
    public FieldBuilder Date() => Type(FieldTypes.Date);
    public FieldBuilder DateTime() => Type(FieldTypes.DateTime);
    /// <summary>The field holds a user id (assignable, shown as the user's name).</summary>
    public FieldBuilder User() => Type(FieldTypes.User);

    /// <summary>An enum field with options from a CLR enum (labels humanised from member names).</summary>
    public FieldBuilder Enum<TEnum>() where TEnum : struct, System.Enum => Enum(typeof(TEnum));

    public FieldBuilder Enum(Type enumType)
    {
        _type = FieldTypes.Enum;
        _options = System.Enum.GetNames(enumType).Select(n => new FieldOption { Value = n, Label = RegistryBuilder.Humanize(n) }).ToList();
        return this;
    }

    /// <summary>An enum field with explicit stored values and labels.</summary>
    public FieldBuilder Options(params (string Value, string Label)[] options)
    {
        _type = FieldTypes.Enum;
        _options = options.Select(o => new FieldOption { Value = o.Value, Label = o.Label }).ToList();
        return this;
    }

    public FieldBuilder OptionLabel(string value, string label)
    {
        var o = _options?.FirstOrDefault(x => Values.Text(x.Value) == value);
        if (o is not null) _options![_options.IndexOf(o)] = new FieldOption { Value = o.Value, Label = label };
        return this;
    }

    /// <summary>Triggers and condition waits may watch this field.</summary>
    public FieldBuilder Watchable(bool on = true) { _watchable = on; return this; }
    /// <summary><c>setFields</c> steps may write this field.</summary>
    public FieldBuilder Settable(bool on = true) { _settable = on; return this; }
    /// <summary>A lifecycle may own this enum field. Implies watchable.</summary>
    public FieldBuilder LifecycleCapable(bool on = true) { _lifecycle = on; _watchable ??= on; return this; }
    /// <summary>Masked in audit entries and run views.</summary>
    public FieldBuilder Sensitive(bool on = true) { _sensitive = on; return this; }
    public FieldBuilder Hidden() { _readable = false; return this; }
    public FieldBuilder Required(bool on = true) { _required = on; return this; }
    public FieldBuilder Readable() { _readable = true; return this; }

    internal FieldDescriptor Build(Func<object, object?> getter, Action<object, object?>? setter, string path, string? entity = null)
    {
        if (_settable == true && setter is null && _type is not (FieldTypes.Relation or FieldTypes.Collection))
            throw new InvalidOperationException($"Field '{_key}' is marked settable but its property has no public setter (or is a nested path).");
        if (_lifecycle == true && _type != FieldTypes.Enum)
            throw new InvalidOperationException($"Field '{_key}' is marked LifecycleCapable but is not an enum field.");
        return new FieldDescriptor
        {
            Key = _key, Label = _label, Type = _type, Options = _options, Entity = entity, Readable = _readable,
            Watchable = _watchable, Settable = _settable, Lifecycle = _lifecycle, Sensitive = _sensitive, Required = _required,
            Getter = getter, Setter = setter, MemberPath = path,
        };
    }
}

public sealed class ActionBuilder
{
    private readonly string _key;
    private string? _label, _description, _category, _entity;
    private readonly List<ParamDescriptor> _inputs = [];
    private readonly List<ParamDescriptor> _outputs = [];

    internal ActionBuilder(string key) => _key = key;

    public ActionBuilder Label(string label) { _label = label; return this; }
    public ActionBuilder Description(string text) { _description = text; return this; }
    public ActionBuilder Category(string category) { _category = category; return this; }
    /// <summary>The entity the action works on (the workflow's record is passed to it).</summary>
    public ActionBuilder For(string entity) { _entity = entity; return this; }

    public ActionBuilder Input(string key, string label, string type = FieldTypes.Text, bool required = false, string? description = null)
    {
        _inputs.Add(new ParamDescriptor { Key = key, Label = label, Type = type, Required = required ? true : null, Description = description });
        return this;
    }

    public ActionBuilder Output(string key, string label, string type = FieldTypes.Text)
    {
        _outputs.Add(new ParamDescriptor { Key = key, Label = label, Type = type });
        return this;
    }

    internal ActionDescriptor Build(Type handler) => new()
    {
        Key = _key, Label = _label ?? RegistryBuilder.Humanize(_key.Split('.').Last()), Description = _description,
        Category = _category, Entity = _entity, Inputs = [.. _inputs], Outputs = [.. _outputs], HandlerType = handler,
    };
}

public sealed class SignalBuilder
{
    private readonly string _key;
    private string? _label, _entity;
    private readonly List<ParamDescriptor> _fields = [];

    internal SignalBuilder(string key) => _key = key;

    public SignalBuilder Label(string label) { _label = label; return this; }
    public SignalBuilder For(string entity) { _entity = entity; return this; }

    public SignalBuilder Field(string key, string label, string type = FieldTypes.Text)
    {
        _fields.Add(new ParamDescriptor { Key = key, Label = label, Type = type });
        return this;
    }

    internal SignalDescriptor Build() => new() { Key = _key, Label = _label ?? RegistryBuilder.Humanize(_key.Split('.').Last()), Entity = _entity, Fields = [.. _fields] };
}
