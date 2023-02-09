﻿using System;
using System.IO;
using Carbon.Utility;

/*
 *
 * Copyright (c) 2022-2023 Carbon Community 
 * All rights reserved.
 *
 */

namespace Doorstop;

public class Entrypoint
{
	public static void Start()
	{
		References.Load();

		Logger.Log(">> Carbon.Doorstop using UnityDoorstop entrypoint");

		try
		{
			Publicizer.Read(
				Path.Combine(Context.RustManaged, "Assembly-CSharp.dll")
			);

			if (!Publicizer.IsPublic("ServerMgr", "Shutdown"))
			{
				Logger.Warn("Assembly is not publicized");
				Publicizer.Publicize(module =>
				{
					try
					{
						Injector.Inject(module);
					}
					catch (Exception ex)
					{
						Logger.Error($"Failed injecting: {ex}");
					}
				});
				Publicizer.Write(
					Path.Combine(Context.RustManaged, "Assembly-CSharp.dll")
				);
			}
			else
			{
				Logger.Log("All validation checks passed");
			}
		}
		catch { /* exit */ }
	}
}
