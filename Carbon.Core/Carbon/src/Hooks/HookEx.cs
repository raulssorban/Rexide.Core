﻿using System;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

/*
 *
 * Copyright (c) 2022-2023 Carbon Community 
 * All rights reserved.
 *
 */

namespace Carbon.Hooks;

public class HookEx : IDisposable
{
	private HookRuntime _runtime;
	private readonly TypeInfo _patchMethod;

	public string HookName
	{ get; }

	public HookFlags Options
	{ get; }

	public Type TargetType
	{ get; }

	public string TargetMethod
	{ get; }

	public Type[] TargetMethodArgs
	{ get; }

	public string Identifier
	{ get; }

	public string ShortIdentifier
	{ get => Identifier.Substring(0, 6); }

	public string Checksum
	{ get; }

	public string[] Dependencies
	{ get; }


	public bool IsStaticHook
	{ get => Options.HasFlag(HookFlags.Static); }

	public bool IsHidden
	{ get => Options.HasFlag(HookFlags.Hidden); }

	public bool IsChecksumIgnored
	{ get => Options.HasFlag(HookFlags.IgnoreChecksum); }

	public bool IsLoaded
	{ get => _runtime.Status != HookState.Inactive; }

	public bool IsInstalled
	{ get => _runtime.Status is HookState.Success or HookState.Warning; }


	public override string ToString()
		=> $"{HookName}[{ShortIdentifier}]";

	public bool HasDependencies()
		=> Dependencies is { Length: > 0 };

	public string PatchMethodName
	{ get => _patchMethod.Name; }

	public HookState Status
	{ get => _runtime.Status; set => _runtime.Status = value; }

	public Exception LastError
	{ get => _runtime.LastError; set => _runtime.LastError = value; }

	public HookEx(TypeInfo type)
	{
		try
		{
			if (type == null || !Attribute.IsDefined(type, typeof(HookAttribute.Patch), false))
				throw new Exception($"Type is null or metadata not defined");

			HookAttribute.Patch metadata = type.GetCustomAttribute<HookAttribute.Patch>() ?? null;

			if (metadata == default)
				throw new Exception($"Metadata information is invalid or was not found");

			HookName = metadata.Name;
			TargetType = metadata.Target;
			TargetMethod = metadata.Method;
			TargetMethodArgs = metadata.MethodArgs;

			if (Attribute.IsDefined(type, typeof(HookAttribute.Identifier), false))
				Identifier = type.GetCustomAttribute<HookAttribute.Identifier>()?.Value ?? $"{Guid.NewGuid():N}";

			if (Attribute.IsDefined(type, typeof(HookAttribute.Options), false))
				Options = type.GetCustomAttribute<HookAttribute.Options>()?.Value ?? HookFlags.None;

			if (Attribute.IsDefined(type, typeof(HookAttribute.Dependencies), false))
				Dependencies = type.GetCustomAttribute<HookAttribute.Dependencies>()?.Value ?? default;

			if (Attribute.IsDefined(type, typeof(HookAttribute.Checksum), false))
				Checksum = type.GetCustomAttribute<HookAttribute.Checksum>()?.Value ?? default;

			_patchMethod = type;
			_runtime.HarmonyHandler = new HarmonyLib.Harmony(Identifier);

			// cache the additional metadata about the hook
			_runtime.Prefix = HarmonyLib.AccessTools.Method(type, "Prefix") ?? null;
			_runtime.Postfix = HarmonyLib.AccessTools.Method(type, "Postfix") ?? null;
			_runtime.Transpiler = HarmonyLib.AccessTools.Method(type, "Transpiler") ?? null;

			if (_runtime.Prefix is null && _runtime.Postfix is null && _runtime.Transpiler is null)
				throw new Exception($"Patch method not found");
		}
		catch (System.Exception e)
		{
			Carbon.Logger.Error($"Error while parsing '{type.Name}'", e);
			return;
		}
	}

	public bool ApplyPatch()
	{
		try
		{
			if (IsInstalled) return true;

			MethodInfo original = HarmonyLib.AccessTools.Method(
				TargetType, TargetMethod, TargetMethodArgs) ?? null;

			if (original is null)
				throw new Exception($"Target method not found");

			bool hasValidChecksum = (IsChecksumIgnored) || IsChecksumValid(original, Checksum);

			MethodInfo current = _runtime.HarmonyHandler.Patch(original,
				prefix: _runtime.Prefix == null ? null : new HarmonyLib.HarmonyMethod(_runtime.Prefix),
				postfix: _runtime.Postfix == null ? null : new HarmonyLib.HarmonyMethod(_runtime.Postfix),
				transpiler: _runtime.Transpiler == null ? null : new HarmonyLib.HarmonyMethod(_runtime.Transpiler)
			) ?? null;

			if (current is null)
				throw new Exception($"Harmony failed to execute");

			// the checksum system needs some lovin..
			// for now let's mark them all as valid
			_runtime.Status = HookState.Success;

			// if (hasValidChecksum)
			// {
			// 	_runtime.Status = HookState.Success;
			// }
			// else
			// {
			// 	Logger.Warn($"Checksum validation failed for '{TargetType.Name}.{TargetMethod}'");
			// 	_runtime.Status = HookState.Warning;
			// }

			Logger.Debug($"Hook '{HookName}[{Identifier}]' patched '{TargetType.Name}.{TargetMethod}'", 2);
		}
#if DEBUG
		catch (HarmonyException e)
		{
			StringBuilder sb = new StringBuilder();
			Logger.Error($"Error while patching hook '{HookName}' index:{e.GetErrorIndex()} offset:{e.GetErrorOffset()}", e);
			sb.AppendLine($"{e.InnerException?.Message.Trim() ?? string.Empty}");

			int x = 0;
			foreach (var instruction in e.GetInstructionsWithOffsets())
				sb.AppendLine($"\t{x++:000} {instruction.Key.ToString("X4")}: {instruction.Value}");

			Logger.Error(sb.ToString());
			sb = default;
		}
#endif
		catch (System.Exception e)
		{
			Logger.Error($"Error while patching hook '{HookName}'", e);
			_runtime.Status = HookState.Failure;
			_runtime.LastError = e;
			return false;
		}

		return true;
	}

	public bool RemovePatch()
	{
		try
		{
			if (!IsInstalled) return true;
			_runtime.HarmonyHandler.UnpatchAll(Identifier);

			Logger.Debug($"Hook '{HookName}[{Identifier}]' unpatched '{TargetType.Name}.{TargetMethod}'", 2);
			_runtime.Status = HookState.Inactive;
			return true;
		}
		catch (System.Exception e)
		{
			Logger.Error($"Error while unpatching hook '{HookName}'", e);
			_runtime.Status = HookState.Failure;
			_runtime.LastError = e;
			return false;
		}
	}

	private static bool IsChecksumValid(MethodBase original, string checksum)
	{
		using SHA1Managed sha1 = new SHA1Managed();
		byte[] bytes = sha1.ComputeHash(original.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>());
		string hash = string.Concat(bytes.Select(b => b.ToString("x2")));
		return hash.Equals(checksum, StringComparison.InvariantCultureIgnoreCase);
	}

	public void SetStatus(HookState Status, Exception e = null)
	{
		_runtime.Status = Status;
		_runtime.LastError = e;
	}

	public void Dispose()
	{
		RemovePatch();
		_runtime.HarmonyHandler = null;
	}
}
