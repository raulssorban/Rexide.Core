﻿// #define DEBUG_VERBOSE

using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Carbon.Base;
using Carbon.Core;
using K4os.Compression.LZ4.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/*
 *
 * Copyright (c) 2022-2023 Carbon Community 
 * All rights reserved.
 *
 */

namespace Carbon.Jobs;

public class ScriptCompilationThread : BaseThreadedJob
{
	public string FilePath;
	public string FileName;
	public string Source;
	public string[] References;
	public string[] Requires;
	public bool IsExtension;
	public List<string> Usings = new ();
	public Dictionary<Type, List<string>> Hooks = new ();
	public Dictionary<Type, List<string>> UnsupportedHooks = new ();
	public Dictionary<Type, List<HookMethodAttribute>> HookMethods = new ();
	public Dictionary<Type, List<PluginReferenceAttribute>> PluginReferences = new ();
	public float CompileTime;
	public Assembly Assembly;
	public List<CompilerException> Exceptions = new();
	internal DateTime TimeSinceCompile;
	internal static Dictionary<string, byte[]> _compilationCache = new();
	internal static Dictionary<string, byte[]> _extensionCompilationCache = new();
	internal static Dictionary<string, PortableExecutableReference> _referenceCache = new();
	internal static Dictionary<string, PortableExecutableReference> _extensionReferenceCache = new();

	internal static byte[] _getPlugin(string name)
	{
		name = name.Replace(" ", "");

		if (!_compilationCache.TryGetValue(name, out var result)) return null;

		return result;
	}
	internal static byte[] _getExtensionPlugin(string name)
	{
		if (!_extensionCompilationCache.TryGetValue(name, out var result)) return null;

		return result;
	}

	internal static void _overridePlugin(string name, byte[] pluginAssembly)
	{
		name = name.Replace(" ", "");

		if (pluginAssembly == null) return;

		var plugin = _getPlugin(name);
		if (plugin == null)
		{
			try { _compilationCache.Add(name, pluginAssembly); } catch { }
			return;
		}

		Array.Clear(plugin, 0, plugin.Length);
		try { _compilationCache[name] = pluginAssembly; } catch { }
	}
	internal static void _overrideExtensionPlugin(string name, byte[] pluginAssembly)
	{
		if (pluginAssembly == null) return;

		var plugin = _getExtensionPlugin(name);
		if (plugin == null)
		{
			try { _extensionCompilationCache.Add(name, pluginAssembly); } catch { }
			return;
		}

		Array.Clear(plugin, 0, plugin.Length);
		try { _extensionCompilationCache[name] = pluginAssembly; } catch { }
	}
	internal static void _clearExtensionPlugin(string name)
	{
		if (_extensionCompilationCache.ContainsKey(name)) _extensionCompilationCache.Remove(name);
		if (_extensionReferenceCache.ContainsKey(name)) _extensionReferenceCache.Remove(name);
	}

	internal void _injectReference(string id, string name, List<MetadataReference> references)
	{
		if (_referenceCache.TryGetValue(name, out var reference))
		{
			references.Add(reference);
		}
		else
		{
			Logger.Debug(id, $"Added common reference '{name}'", 4);
			var raw = Supervisor.ASM.ReadAssembly(name);
			using var mem = new MemoryStream(raw);
			var processedReference = MetadataReference.CreateFromStream(mem);
			references.Add(processedReference);
			_referenceCache.Add(name, processedReference);
		}
	}
	internal void _injectExtensionReference(string id, string name, List<MetadataReference> references)
	{
		if(!_extensionCompilationCache.TryGetValue(name, out var raw ))
		{
			return;
		}

		if (_extensionReferenceCache.TryGetValue(name, out var reference))
		{
			references.Add(reference);
		}
		else
		{
			Logger.Debug(id, $"Added common Extension reference '{name}'", 4);
			using var mem = new MemoryStream(raw);
			var processedReference = MetadataReference.CreateFromStream(mem);
			references.Add(processedReference);
			_extensionReferenceCache.Add(name, processedReference);
		}
	}

	internal static MetadataReference _getReferenceFromCache(string reference)
	{
		try
		{
			byte[] raw = Supervisor.ASM.ReadAssembly(reference);
			if (raw == null || raw.Length == 0) throw new ArgumentException();

			using (MemoryStream mem = new MemoryStream(raw))
				return MetadataReference.CreateFromStream(mem);
		}
		catch (System.Exception e)
		{
			Logger.Error($"_getReferenceFromCache('{reference}') failed", e);
			return null;
		}
	}
	internal List<MetadataReference> _addReferences()
	{
		var references = new List<MetadataReference>();
		var id = Path.GetFileNameWithoutExtension(FilePath);

		foreach (var item in CommunityInternal.CompilerReferenceList)
		{
			try
			{
				_injectReference(id, item, references);
			}
			catch (System.Exception)
			{
				Logger.Debug(id, $"Error loading common reference '{item}'", 4);
			}
		}

		if (Community.Runtime.Config.HarmonyReference)
		{
			_injectReference(id, "0Harmony", references);
		}

		foreach(var item in _extensionCompilationCache)
		{
			_injectExtensionReference(id, item.Key, references);
		}

		// goes through the requested use list by the plugin
		foreach (var element in Usings)
		{
			try
			{
				Logger.Debug(id, $"Added using reference '{element}'", 4);
				var outReference = MetadataReference.CreateFromFile(Type.GetType(element).Assembly.Location);
				if (outReference != null && !references.Any(x => x.Display == outReference.Display)) references.Add(outReference);
			}
			catch (System.Exception)
			{
				Logger.Debug(id, $"Error loading using reference '{element}'", 4);
			}
		}

		// goes through the requested references by the plugin
		foreach (var reference in References)
		{
			try
			{
				Logger.Debug(id, $"Added require reference '{reference}'", 2);
				var outReference = _getReferenceFromCache(reference);
				if (outReference != null && !references.Any(x => x.Display == outReference.Display)) references.Add(outReference);
			}
			catch (System.Exception)
			{
				Logger.Debug(id, $"Error loading require reference '{reference}'", 2);
			}
		}

		Logger.Debug(id, $"Compiler will use {references.Count} assembly references", 1);
		return references;
	}

	public class CompilerException : Exception
	{
		public string FilePath;
		public CompilerError Error;
		public CompilerException(string filePath, CompilerError error) { FilePath = filePath; Error = error; }

		public override string ToString()
		{
			return $"{Error.ErrorText}\n ({FilePath} {Error.Column} line {Error.Line})";
		}
	}

	public override void Start()
	{
		base.Start();
	}

	public override void ThreadFunction()
	{
		try
		{
			Exceptions.Clear();
			TimeSinceCompile = DateTime.Now;
			FileName = Path.GetFileNameWithoutExtension(FilePath);

			var trees = new List<SyntaxTree>();

			var parseOptions = new CSharpParseOptions(LanguageVersion.Latest)
				.WithPreprocessorSymbols(Community.Runtime.Config.ConditionalCompilationSymbols);
			var tree = CSharpSyntaxTree.ParseText(
				Source, options: parseOptions);
			trees.Add(tree);

			var root = tree.GetCompilationUnitRoot();

			foreach (var element in root.Usings)
				Usings.Add($"{element.Name}");

			var references = _addReferences();

			foreach (var require in Requires)
			{
				try
				{
					var requiredPlugin = _getPlugin(require);

					using (var dllStream = new MemoryStream(requiredPlugin))
					{
						references.Add(MetadataReference.CreateFromStream(dllStream));
					}
				}
				catch { /* do nothing */ }
			}

			var options = new CSharpCompilationOptions(
				OutputKind.DynamicallyLinkedLibrary,
				optimizationLevel: OptimizationLevel.Release,
				deterministic: true, warningLevel: 4
			);

			var compilation = CSharpCompilation.Create(
				$"Script.{FileName}.{Guid.NewGuid():N}", trees, references, options);

			using (var dllStream = new MemoryStream())
			{
				var emit = compilation.Emit(dllStream);

				foreach (var error in emit.Diagnostics)
				{
					var span = error.Location.GetMappedLineSpan().Span;
					switch (error.Severity)
					{
#if DEBUG_VERBOSE
							case DiagnosticSeverity.Warning:
								Logger.Warn($"Compile error {error.Id} '{FilePath}' @{span.Start.Line + 1}:{span.Start.Character + 1}" +
									Environment.NewLine + error.GetMessage(CultureInfo.InvariantCulture));
								break;
#endif
						case DiagnosticSeverity.Error:
#if DEBUG_VERBOSE
								Logger.Error($"Compile error {error.Id} '{FilePath}' @{span.Start.Line + 1}:{span.Start.Character + 1}" +
									Environment.NewLine + error.GetMessage(CultureInfo.InvariantCulture));
#endif
							Exceptions.Add(new CompilerException(FilePath,
								new CompilerError(FileName, span.Start.Line + 1, span.Start.Character + 1, error.Id, error.GetMessage(CultureInfo.InvariantCulture))));
							break;
					}
				}

				if (emit.Success)
				{
					var assembly = dllStream.ToArray();
					if (assembly != null)
					{
						if(IsExtension) _overrideExtensionPlugin(FilePath, assembly);
						_overridePlugin(FileName, assembly);
						Assembly = Assembly.Load(assembly);
					}
				}
			}

			if (Assembly == null)
			{
				throw null;
			}

			CompileTime = (float)(DateTime.Now - TimeSinceCompile).Milliseconds;

			references.Clear();
			references = null;
			trees.Clear();
			trees = null;

			foreach (var type in Assembly.GetTypes())
			{
				var hooks = new List<string>();
				var unsupportedHooks = new List<string>();
				var hookMethods = new List<HookMethodAttribute>();
				var pluginReferences = new List<PluginReferenceAttribute>();
				Hooks.Add(type, hooks);
				UnsupportedHooks.Add(type, unsupportedHooks);
				HookMethods.Add(type, hookMethods);
				PluginReferences.Add(type, pluginReferences);

				foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
				{
					if (HookValidator.IsIncompatibleOxideHook(method.Name))
					{
						unsupportedHooks.Add(method.Name);
					}

					if (Community.Runtime.HookManager.IsHookLoaded(method.Name))
					{
						if (!hooks.Contains(method.Name)) hooks.Add(method.Name);
					}
					else
					{
						var attribute = method.GetCustomAttribute<HookMethodAttribute>();
						if (attribute == null) continue;

						attribute.Method = method;
						hookMethods.Add(attribute);
					}
				}

				foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
				{
					var attribute = field.GetCustomAttribute<PluginReferenceAttribute>();
					if (attribute == null) continue;

					attribute.Field = field;
					pluginReferences.Add(attribute);
				}
			}

			if (Exceptions.Count > 0) throw null;
		}
		catch (Exception ex) { Logger.Error($"Threading compilation failed for '{FileName}'", ex); }
	}
}
