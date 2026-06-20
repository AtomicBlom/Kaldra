using System.Text;
using System.Xml;
using System.Xml.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Loom.UI.SourceGen;

/// <summary>
/// The XAML → typed control-tree incremental generator (PlanningUI.md §D1.4). For each <c>.xaml</c>
/// <see cref="AdditionalText"/> it emits a <c>partial</c> of the <c>x:Class</c> with typed <c>x:Name</c> fields and an
/// <c>InitializeComponent()</c> that builds the tree with generated <c>new</c> (never reflection) and assigns
/// literal/enum properties resolved against the <see cref="Compilation"/> semantic model.
/// <para>
/// MU2 adds compiled data + event binding (§D1.5/§D1.7): <c>{x:Bind path[, Mode=OneTime|OneWay]}</c> on a property
/// emits a typed <c>&lt;Class&gt;_Bindings</c> helper (Initialize/Update/Subscribe/StopTracking + INPC tracking that
/// switches on <c>PropertyName</c> literals); an event attribute (<c>Click="{x:Bind OnClick}"</c> or bare
/// <c>Click="OnClick"</c>) wires <c>el.Click += this.OnClick</c>, signature-checked against the control's event.
/// TwoWay / functions / converters / fallback / <c>{StaticResource}</c> are out of the MU2 subset and diagnose.
/// </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class XamlSourceGenerator : IIncrementalGenerator
{
	private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

	private static readonly DiagnosticDescriptor ParseError = new("LOOMUI001", "Invalid XAML",
		"XAML parse error: {0}", "Loom.UI", DiagnosticSeverity.Error, true);
	private static readonly DiagnosticDescriptor MissingClass = new("LOOMUI002", "Missing x:Class",
		"The root element needs an x:Class attribute for the generator to emit a partial class", "Loom.UI", DiagnosticSeverity.Error, true);
	private static readonly DiagnosticDescriptor UnknownElement = new("LOOMUI003", "Unknown element",
		"'{0}' is not a known Loom.UI element (looked under Loom.UI.Controls and Loom.UI)", "Loom.UI", DiagnosticSeverity.Error, true);
	private static readonly DiagnosticDescriptor UnknownProperty = new("LOOMUI004", "Unknown property",
		"'{0}' has no settable property '{1}'", "Loom.UI", DiagnosticSeverity.Error, true);
	private static readonly DiagnosticDescriptor UnsupportedProperty = new("LOOMUI005", "Unsupported property type",
		"Property '{0}' of type '{1}' is outside the MU0 XAML subset (string / numeric / bool / enum)", "Loom.UI", DiagnosticSeverity.Error, true);
	private static readonly DiagnosticDescriptor NoContentModel = new("LOOMUI006", "Element cannot hold children",
		"'{0}' has no Children/Child/Content to host the child '{1}'", "Loom.UI", DiagnosticSeverity.Error, true);
	private static readonly DiagnosticDescriptor UnsupportedMarkup = new("LOOMUI007", "Unsupported markup",
		"Markup '{0}' is outside the MU2 XAML subset (only {{x:Bind path, Mode=OneTime|OneWay}} + event handlers)", "Loom.UI", DiagnosticSeverity.Error, true);
	private static readonly DiagnosticDescriptor BindPathUnresolved = new("LOOMUI008", "Unresolved x:Bind path",
		"x:Bind path '{0}' could not be resolved on '{1}'", "Loom.UI", DiagnosticSeverity.Error, true);
	private static readonly DiagnosticDescriptor BindTypeMismatch = new("LOOMUI009", "Incompatible x:Bind types",
		"x:Bind source type '{0}' is not assignable to target property '{1}' of type '{2}'", "Loom.UI", DiagnosticSeverity.Error, true);
	private static readonly DiagnosticDescriptor EventHandlerSignature = new("LOOMUI010", "Bad event handler",
		"'{0}' has no handler '{1}(object, RoutedEventArgs)' for event '{2}'", "Loom.UI", DiagnosticSeverity.Error, true);

	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		var xaml = context.AdditionalTextsProvider.Where(a => a.Path.EndsWith(".xaml", System.StringComparison.OrdinalIgnoreCase));
		var combined = xaml.Combine(context.CompilationProvider);
		context.RegisterSourceOutput(combined, (spc, pair) => Generate(spc, pair.Right, pair.Left));
	}

	// One resolved x:Bind on a property: assign target.Prop = <read of path on the page>, optionally INPC-tracked.
	private sealed class Bind(string targetVar, string targetProp, string[] path, ITypeSymbol[] resultTypes, ITypeSymbol leafType, bool oneWay)
	{
		public string TargetVar { get; } = targetVar;
		public string TargetProp { get; } = targetProp;
		public string[] Path { get; } = path;
		public ITypeSymbol[] ResultTypes { get; } = resultTypes;   // resultTypes[i] = type after accessing Path[i]
		public ITypeSymbol LeafType { get; } = leafType;
		public bool OneWay { get; } = oneWay;
	}

	private sealed class Gen(Compilation compilation, SourceProductionContext spc, AdditionalText file, INamedTypeSymbol? pageType)
	{
		public Compilation Compilation { get; } = compilation;
		public SourceProductionContext Spc { get; } = spc;
		public AdditionalText File { get; } = file;
		public INamedTypeSymbol? PageType { get; } = pageType;
		public List<string> Fields { get; } = [];
		public StringBuilder Body { get; } = new();
		public List<string> EventWires { get; } = [];   // "Btn.Click += this.OnClick;" lines (in InitializeComponent)
		public List<Bind> Bindings { get; } = [];
		public int Counter;
	}

	private static void Generate(SourceProductionContext spc, Compilation compilation, AdditionalText file)
	{
		var src = file.GetText(spc.CancellationToken)?.ToString();
		if (string.IsNullOrWhiteSpace(src))
			return;

		XDocument doc;
		try { doc = XDocument.Parse(src!, LoadOptions.SetLineInfo); }
		catch (XmlException ex) { spc.ReportDiagnostic(Diagnostic.Create(ParseError, Loc(file, 1, 1), ex.Message)); return; }

		var root = doc.Root;
		if (root is null)
			return;

		var classAttr = root.Attribute(X + "Class");
		if (classAttr is null) { spc.ReportDiagnostic(Diagnostic.Create(MissingClass, Loc(file, root))); return; }

		var (ns, cls) = SplitClass(classAttr.Value);
		var pageType = compilation.GetTypeByMetadataName(classAttr.Value);
		var gen = new Gen(compilation, spc, file, pageType);

		var rootVar = EmitElement(gen, root);
		if (rootVar is null)
			return;
		var rootType = ResolveType(compilation, root.Name.LocalName);
		var rootTypeName = rootType?.ToDisplayString() ?? "global::Loom.UI.UIElement";

		var hasBindings = gen.Bindings.Count > 0;

		var sb = new StringBuilder();
		sb.AppendLine("// <auto-generated/> by Loom.UI.SourceGen");
		sb.AppendLine("#nullable enable");
		if (ns is not null)
			sb.AppendLine($"namespace {ns};");
		sb.AppendLine();
		sb.AppendLine($"partial class {cls}");
		sb.AppendLine("{");
		foreach (var f in gen.Fields)
			sb.AppendLine($"\t{f}");
		sb.AppendLine($"\tinternal {rootTypeName} Root = null!;");
		if (hasBindings)
			sb.AppendLine($"\tprivate {cls}_Bindings? __bindings;");
		sb.AppendLine();
		sb.AppendLine("\tpublic void InitializeComponent()");
		sb.AppendLine("\t{");
		sb.Append(gen.Body);
		foreach (var wire in gen.EventWires)
			sb.AppendLine($"\t\t{wire}");
		sb.AppendLine($"\t\tRoot = {rootVar};");
		if (hasBindings)
		{
			sb.AppendLine($"\t\t__bindings = new {cls}_Bindings(this);");
			sb.AppendLine("\t\t__bindings.Initialize();");
		}
		sb.AppendLine("\t}");
		sb.AppendLine("}");

		spc.AddSource($"{cls}.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));

		if (hasBindings)
			EmitBindings(spc, ns, cls, gen.Bindings);
	}

	// Emit construction for one element; returns the C# variable holding it (a field for x:Name'd / bound / evented).
	private static string? EmitElement(Gen gen, XElement el)
	{
		var type = ResolveType(gen.Compilation, el.Name.LocalName);
		if (type is null) { gen.Spc.ReportDiagnostic(Diagnostic.Create(UnknownElement, Loc(gen.File, el), el.Name.LocalName)); return null; }

		var typeName = type.ToDisplayString();
		var nameAttr = el.Attribute(X + "Name");

		// Bound or evented elements must be reachable from the Bindings helper / InitializeComponent → make a field.
		var needsField = nameAttr is not null || NeedsField(type, el);
		string var;
		if (nameAttr is not null)
		{
			var = nameAttr.Value;
			gen.Fields.Add($"internal {typeName} {var} = null!;");
			gen.Body.AppendLine($"\t\t{var} = new {typeName}();");
		}
		else if (needsField)
		{
			var = $"__b{gen.Counter++}";
			gen.Fields.Add($"internal {typeName} {var} = null!;");
			gen.Body.AppendLine($"\t\t{var} = new {typeName}();");
		}
		else
		{
			var = $"__e{gen.Counter++}";
			gen.Body.AppendLine($"\t\tvar {var} = new {typeName}();");
		}

		foreach (var attr in el.Attributes())
		{
			if (attr.IsNamespaceDeclaration || attr.Name.Namespace == X)
				continue;
			if (attr.Name.Namespace != XNamespace.None || attr.Name.LocalName.Contains('.'))
				continue; // attached / property-element syntax: out of the MU2 subset

			var name = attr.Name.LocalName;
			var value = attr.Value;

			// Event handler? (the attribute name matches an event on the element)
			var evt = FindEvent(type, name);
			if (evt is not null)
			{
				var handler = ExtractEventHandler(gen, attr, value);
				if (handler is null)
					continue;
				if (!HandlerSignatureOk(gen, handler))
					gen.Spc.ReportDiagnostic(Diagnostic.Create(EventHandlerSignature, Loc(gen.File, attr), gen.PageType?.ToDisplayString() ?? "the page", handler, name));
				gen.EventWires.Add($"{var}.{name} += this.{handler};");
				continue;
			}

			// x:Bind on a property?
			if (IsMarkup(value, out var inner))
			{
				if (!TryParseXBind(inner, out var path, out var oneWay))
				{
					gen.Spc.ReportDiagnostic(Diagnostic.Create(UnsupportedMarkup, Loc(gen.File, attr), value.Trim()));
					continue;
				}
				var prop = FindProperty(type, name);
				if (prop is null) { gen.Spc.ReportDiagnostic(Diagnostic.Create(UnknownProperty, Loc(gen.File, attr), typeName, name)); continue; }
				if (!TryResolvePath(gen.PageType, path, out var resultTypes, out var leafType))
				{
					gen.Spc.ReportDiagnostic(Diagnostic.Create(BindPathUnresolved, Loc(gen.File, attr), string.Join(".", path), gen.PageType?.ToDisplayString() ?? "the page"));
					continue;
				}
				if (!gen.Compilation.ClassifyConversion(leafType, prop.Type).IsImplicit)
				{
					gen.Spc.ReportDiagnostic(Diagnostic.Create(BindTypeMismatch, Loc(gen.File, attr), leafType.ToDisplayString(), name, prop.Type.ToDisplayString()));
					continue;
				}
				gen.Bindings.Add(new Bind(var, prop.Name, path, resultTypes, leafType, oneWay));
				continue;
			}

			// Plain literal / enum value.
			var prop2 = FindProperty(type, name);
			if (prop2 is null) { gen.Spc.ReportDiagnostic(Diagnostic.Create(UnknownProperty, Loc(gen.File, attr), typeName, name)); continue; }
			var literal = Literal(prop2.Type, value);
			if (literal is null) { gen.Spc.ReportDiagnostic(Diagnostic.Create(UnsupportedProperty, Loc(gen.File, attr), name, prop2.Type.ToDisplayString())); continue; }
			gen.Body.AppendLine($"\t\t{var}.{prop2.Name} = {literal};");
		}

		// Child elements (skip property-element syntax for MU2).
		var kind = ContainerKind(type);
		foreach (var childEl in el.Elements())
		{
			if (childEl.Name.LocalName.Contains('.'))
				continue;
			var childVar = EmitElement(gen, childEl);
			if (childVar is null)
				continue;
			switch (kind)
			{
				case "Children": gen.Body.AppendLine($"\t\t{var}.Children.Add({childVar});"); break;
				case "Child": gen.Body.AppendLine($"\t\t{var}.Child = {childVar};"); break;
				case "Content": gen.Body.AppendLine($"\t\t{var}.Content = {childVar};"); break;
				default: gen.Spc.ReportDiagnostic(Diagnostic.Create(NoContentModel, Loc(gen.File, childEl), typeName, childEl.Name.LocalName)); break;
			}
		}

		return var;
	}

	// --- MU2: the generated Bindings helper (PlanningUI.md §D1.5/§D1.7) ---

	private static void EmitBindings(SourceProductionContext spc, string? ns, string cls, List<Bind> binds)
	{
		var sb = new StringBuilder();
		sb.AppendLine("// <auto-generated/> by Loom.UI.SourceGen");
		sb.AppendLine("#nullable enable");
		sb.AppendLine("using System.Collections.Generic;");
		sb.AppendLine("using System.ComponentModel;");
		if (ns is not null)
			sb.AppendLine($"namespace {ns};");
		sb.AppendLine();
		sb.AppendLine($"internal sealed class {cls}_Bindings");
		sb.AppendLine("{");
		sb.AppendLine($"\tprivate readonly {cls} _root;");
		sb.AppendLine("\tprivate readonly List<INotifyPropertyChanged> _tracked = new();");
		sb.AppendLine($"\tpublic {cls}_Bindings({cls} root) => _root = root;");
		sb.AppendLine();

		var oneWay = binds.Where(b => b.OneWay).ToList();

		// Initialize: OneTime assignments, then subscribe + push the OneWay values.
		sb.AppendLine("\tpublic void Initialize()");
		sb.AppendLine("\t{");
		foreach (var b in binds.Where(b => !b.OneWay))
			sb.AppendLine($"\t\t{Assignment(b)}");
		sb.AppendLine("\t\tSubscribe();");
		sb.AppendLine("\t\tUpdate();");
		sb.AppendLine("\t}");
		sb.AppendLine();

		// Update: push every OneWay value via its typed setter.
		for (var i = 0; i < oneWay.Count; i++)
			sb.AppendLine($"\tprivate void Update_{i}() => {Assignment(oneWay[i])}");
		sb.AppendLine("\tpublic void Update()");
		sb.AppendLine("\t{");
		for (var i = 0; i < oneWay.Count; i++)
			sb.AppendLine($"\t\tUpdate_{i}();");
		sb.AppendLine("\t}");
		sb.AppendLine();

		// Subscribe to each distinct INPC source object (the page + each 2-hop intermediate owner).
		var trackExprs = new List<string> { "_root" };
		foreach (var b in oneWay)
			if (b.Path.Length == 2)
			{
				var owner = "_root." + b.Path[0];
				if (!trackExprs.Contains(owner))
					trackExprs.Add(owner);
			}

		sb.AppendLine("\tpublic void Subscribe()");
		sb.AppendLine("\t{");
		sb.AppendLine("\t\tStopTracking();");
		foreach (var expr in trackExprs)
			sb.AppendLine($"\t\tTrack({expr});");
		sb.AppendLine("\t}");
		sb.AppendLine();
		sb.AppendLine("\tpublic void StopTracking()");
		sb.AppendLine("\t{");
		sb.AppendLine("\t\tforeach (var t in _tracked) t.PropertyChanged -= OnPropertyChanged;");
		sb.AppendLine("\t\t_tracked.Clear();");
		sb.AppendLine("\t}");
		sb.AppendLine();
		sb.AppendLine("\tprivate void Track(object? o)");
		sb.AppendLine("\t{");
		sb.AppendLine("\t\tif (o is INotifyPropertyChanged inpc) { inpc.PropertyChanged += OnPropertyChanged; _tracked.Add(inpc); }");
		sb.AppendLine("\t}");
		sb.AppendLine();

		// INPC change handler: switch on PropertyName string literals → the specific Update_N (a leaf change), or a
		// re-subscribe + full update (an intermediate-hop change, or the WinUI "all changed" null/empty signal).
		var intermediateNames = new HashSet<string>();
		foreach (var b in oneWay)
			if (b.Path.Length == 2)
				intermediateNames.Add(b.Path[0]);
		var leafUpdates = new Dictionary<string, List<int>>();
		for (var i = 0; i < oneWay.Count; i++)
		{
			var segs = oneWay[i].Path;
			var leaf = segs[segs.Length - 1];
			if (intermediateNames.Contains(leaf))
				continue; // an intermediate-name change already re-evaluates everything
			if (!leafUpdates.TryGetValue(leaf, out var list))
				leafUpdates[leaf] = list = [];
			list.Add(i);
		}

		sb.AppendLine("\tprivate void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)");
		sb.AppendLine("\t{");
		sb.AppendLine("\t\tswitch (e.PropertyName)");
		sb.AppendLine("\t\t{");
		foreach (var name in intermediateNames)
			sb.AppendLine($"\t\t\tcase \"{name}\": Subscribe(); Update(); break;");
		foreach (var kv in leafUpdates)
		{
			var calls = string.Join(" ", kv.Value.Select(i => $"Update_{i}();"));
			sb.AppendLine($"\t\t\tcase \"{kv.Key}\": {calls} break;");
		}
		sb.AppendLine("\t\t\tcase null: case \"\": Subscribe(); Update(); break;");
		sb.AppendLine("\t\t}");
		sb.AppendLine("\t}");
		sb.AppendLine("}");

		spc.AddSource($"{cls}.Bindings.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
	}

	// "Target.Prop = <typed read of the page path>;" — coalescing a null string and a deep-path-nullable value.
	private static string Assignment(Bind b)
	{
		var read = ReadExpr(b);
		string value;
		if (b.LeafType.SpecialType == SpecialType.System_String)
			value = $"({read}) ?? string.Empty";
		else if (read.Contains("?.") && b.LeafType.IsValueType)
			value = $"({read}) ?? default";
		else
			value = read;
		return $"_root.{b.TargetVar}.{b.TargetProp} = {value};";
	}

	// "_root.Seg0?.Seg1..." — `?.` after a reference-typed hop so a null mid-path short-circuits to the fallback.
	private static string ReadExpr(Bind b)
	{
		var sb = new StringBuilder("_root");
		for (var i = 0; i < b.Path.Length; i++)
		{
			var acc = i == 0 ? "." : (b.ResultTypes[i - 1].IsReferenceType ? "?." : ".");
			sb.Append(acc).Append(b.Path[i]);
		}
		return sb.ToString();
	}

	// --- Resolution helpers ---

	private static bool NeedsField(INamedTypeSymbol type, XElement el)
	{
		foreach (var attr in el.Attributes())
		{
			if (attr.IsNamespaceDeclaration || attr.Name.Namespace == X || attr.Name.Namespace != XNamespace.None || attr.Name.LocalName.Contains('.'))
				continue;
			if (FindEvent(type, attr.Name.LocalName) is not null)
				return true;
			if (IsMarkup(attr.Value, out var inner) && inner.TrimStart().StartsWith("x:Bind"))
				return true;
		}
		return false;
	}

	private static bool IsMarkup(string value, out string inner)
	{
		var t = value.Trim();
		if (t.Length >= 2 && t[0] == '{' && t[t.Length - 1] == '}')
		{
			inner = t.Substring(1, t.Length - 2).Trim();
			return true;
		}
		inner = "";
		return false;
	}

	// Parse "x:Bind path[, Mode=OneTime|OneWay]"; reject TwoWay / functions / converters / fallback / non-x:Bind.
	private static bool TryParseXBind(string inner, out string[] path, out bool oneWay)
	{
		path = [];
		oneWay = false;
		var parts = inner.Split(',');
		var head = parts[0].Trim();
		if (!head.StartsWith("x:Bind"))
			return false;
		var p = head.Substring("x:Bind".Length).Trim();
		if (p.Length == 0 || p.Contains('(') || p.Contains(' '))
			return false; // functions / spaces not in the MU2 subset
		var segs = p.Split('.');
		foreach (var s in segs)
			if (!IsIdentifier(s))
				return false;
		if (segs.Length > 2)
			return false; // MU2 supports up to 2-hop paths
		for (var i = 1; i < parts.Length; i++)
		{
			var kv = parts[i].Split('=');
			if (kv.Length != 2)
				return false;
			var key = kv[0].Trim();
			var val = kv[1].Trim();
			if (key != "Mode")
				return false; // Converter / FallbackValue / TargetNullValue are MU3
			if (val == "OneWay")
				oneWay = true;
			else if (val == "OneTime")
				oneWay = false;
			else
				return false; // TwoWay is MU3
		}
		path = segs;
		return true;
	}

	private static bool IsIdentifier(string s)
	{
		if (s.Length == 0 || !(char.IsLetter(s[0]) || s[0] == '_'))
			return false;
		foreach (var c in s)
			if (!(char.IsLetterOrDigit(c) || c == '_'))
				return false;
		return true;
	}

	// Resolve a member path against the page type → the result type after each hop + the leaf type.
	private static bool TryResolvePath(INamedTypeSymbol? pageType, string[] path, out ITypeSymbol[] resultTypes, out ITypeSymbol leafType)
	{
		resultTypes = [];
		leafType = null!;
		if (pageType is null)
			return false;
		var types = new ITypeSymbol[path.Length];
		ITypeSymbol cur = pageType;
		for (var i = 0; i < path.Length; i++)
		{
			if (cur is not INamedTypeSymbol named || FindReadable(named, path[i]) is not { } memberType)
				return false;
			types[i] = memberType;
			cur = memberType;
		}
		resultTypes = types;
		leafType = types[types.Length - 1];
		return true;
	}

	private static ITypeSymbol? FindReadable(INamedTypeSymbol type, string name)
	{
		for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
		{
			var p = t.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault(p => p.GetMethod is not null);
			if (p is not null)
				return p.Type;
			var f = t.GetMembers(name).OfType<IFieldSymbol>().FirstOrDefault();
			if (f is not null)
				return f.Type;
		}
		return null;
	}

	private static IEventSymbol? FindEvent(INamedTypeSymbol type, string name)
	{
		for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
		{
			var e = t.GetMembers(name).OfType<IEventSymbol>().FirstOrDefault();
			if (e is not null)
				return e;
		}
		return null;
	}

	private static string? ExtractEventHandler(Gen gen, XAttribute attr, string value)
	{
		if (!IsMarkup(value, out var inner))
			return value.Trim(); // bare handler name: Click="OnClick"
		if (inner.TrimStart().StartsWith("x:Bind"))
		{
			var h = inner.Substring(inner.IndexOf("x:Bind", System.StringComparison.Ordinal) + "x:Bind".Length).Trim();
			if (IsIdentifier(h))
				return h;
		}
		gen.Spc.ReportDiagnostic(Diagnostic.Create(UnsupportedMarkup, Loc(gen.File, attr), value.Trim()));
		return null;
	}

	// The handler must be a void M(object, RoutedEventArgs) on the page (best-effort when the page type is unresolved).
	private static bool HandlerSignatureOk(Gen gen, string handler)
	{
		if (gen.PageType is null)
			return true;
		for (INamedTypeSymbol? t = gen.PageType; t is not null; t = t.BaseType)
			foreach (var m in t.GetMembers(handler).OfType<IMethodSymbol>())
				if (m.ReturnsVoid && m.Parameters.Length == 2 &&
					m.Parameters[0].Type.SpecialType == SpecialType.System_Object &&
					m.Parameters[1].Type.Name == "RoutedEventArgs")
					return true;
		return false;
	}

	private static INamedTypeSymbol? ResolveType(Compilation c, string localName) =>
		c.GetTypeByMetadataName($"Loom.UI.Controls.{localName}") ?? c.GetTypeByMetadataName($"Loom.UI.{localName}");

	private static IPropertySymbol? FindProperty(INamedTypeSymbol type, string name)
	{
		for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
		{
			var p = t.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault(p => p.SetMethod is not null);
			if (p is not null)
				return p;
		}
		return null;
	}

	// How an element hosts children: a Panel exposes Children, a Border exposes Child, a ContentControl exposes Content.
	private static string? ContainerKind(INamedTypeSymbol type)
	{
		foreach (var name in new[] { "Children", "Child", "Content" })
			for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
				if (t.GetMembers(name).OfType<IPropertySymbol>().Any())
					return name;
		return null;
	}

	private static string? Literal(ITypeSymbol type, string value)
	{
		if (type.TypeKind == TypeKind.Enum)
			return $"{type.ToDisplayString()}.{value.Trim()}";
		return type.SpecialType switch
		{
			SpecialType.System_String => SyntaxFactory.Literal(value).ToString(),
			SpecialType.System_Object => SyntaxFactory.Literal(value).ToString(), // Content="text" → string (ContentControl wraps it)
			SpecialType.System_Boolean => value.Trim().ToLowerInvariant(),
			SpecialType.System_Single => $"{value.Trim()}f",
			SpecialType.System_Double => $"{value.Trim()}d",
			SpecialType.System_Int32 => value.Trim(),
			_ => null,
		};
	}

	private static (string? ns, string cls) SplitClass(string full)
	{
		var dot = full.LastIndexOf('.');
		return dot < 0 ? (null, full) : (full.Substring(0, dot), full.Substring(dot + 1));
	}

	private static Location Loc(AdditionalText file, XObject node)
	{
		var li = (IXmlLineInfo)node;
		return li.HasLineInfo() ? Loc(file, li.LineNumber, li.LinePosition) : Loc(file, 1, 1);
	}

	private static Location Loc(AdditionalText file, int line, int col)
	{
		var pos = new LinePosition(System.Math.Max(0, line - 1), System.Math.Max(0, col - 1));
		return Location.Create(file.Path, new TextSpan(0, 0), new LinePositionSpan(pos, pos));
	}
}
