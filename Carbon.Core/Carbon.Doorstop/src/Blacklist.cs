﻿using System.Text.RegularExpressions;

/*
 *
 * Copyright (c) 2022-2023 Carbon Community 
 * All rights reserved.
 *
 */

namespace Carbon.Utility;

internal static class Blacklist
{

	private static readonly string[] Items =
	{
		@"^SpawnGroup.GetSpawnPoint$",
		@"^SpawnGroup.Spawn$",
		@"^SpawnGroup.PostSpawnProcess$",
		@"^ScientistNPC.OverrideCorpseName$",
		@"^TriggerParentElevator.IsClipping$"
	};

	internal static bool IsBlacklisted(string Name)
	{
		foreach (string Item in Items)
			if (Regex.IsMatch(Name, Item)) return true;
		return false;
	}
}
