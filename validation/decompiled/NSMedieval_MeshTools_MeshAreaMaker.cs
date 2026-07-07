using System;
using System.Collections.Generic;
using NSMedieval.Extensions;
using NSMedieval.Map;
using NSMedieval.Model;
using UnityEngine;

namespace NSMedieval.MeshTools;

public class MeshAreaMaker
{
	public Vec3Int[,] GetMeshArea(Vec3Int inputStart, Vec3Int inputEnd, Func<Vec3Int, bool, bool> condition)
	{
		Vec3Int vec3Int = Vec3Int.Min(in inputStart, in inputEnd);
		Vec3Int vec3Int2 = Vec3Int.Max(in inputStart, in inputEnd);
		int num = vec3Int2.x - vec3Int.x + 1;
		int num2 = vec3Int2.z - vec3Int.z + 1;
		int y = vec3Int.y / World.MapBlockHeight;
		Vec3Int[,] array = new Vec3Int[num, num2];
		for (int i = vec3Int.x; i <= vec3Int2.x; i++)
		{
			for (int j = vec3Int.z; j <= vec3Int2.z; j++)
			{
				Vec3Int vec3Int3 = new Vec3Int(i, y, j);
				if (condition(vec3Int3, arg2: false))
				{
					array[Mathf.Abs(i - vec3Int.x), Mathf.Abs(vec3Int.z - j)] = vec3Int3;
				}
				else
				{
					array[Mathf.Abs(i - vec3Int.x), Mathf.Abs(j - vec3Int.z)] = Vec3Int.zero;
				}
			}
		}
		return FindLargestConnectedComponent(array);
	}

	public Vec3Int[,] GetModifiedMeshArea(Vec3Int inputStart, Vec3Int inputEnd, List<Vec3Int> validPositions, Func<Vec3Int, bool, bool> condition)
	{
		Vec3Int vec3Int = Vec3Int.Min(in inputStart, in inputEnd);
		Vec3Int vec3Int2 = Vec3Int.Max(in inputStart, in inputEnd);
		int num = vec3Int2.x - vec3Int.x + 1;
		int num2 = vec3Int2.z - vec3Int.z + 1;
		int y = vec3Int.y / World.MapBlockHeight;
		Vec3Int[,] array = new Vec3Int[num, num2];
		for (int i = vec3Int.x; i <= vec3Int2.x; i++)
		{
			for (int j = vec3Int.z; j <= vec3Int2.z; j++)
			{
				Vec3Int vec3Int3 = new Vec3Int(i, y, j);
				if (condition(vec3Int3, arg2: true) && validPositions.Contains(vec3Int3))
				{
					array[Mathf.Abs(i - vec3Int.x), Mathf.Abs(vec3Int.z - j)] = vec3Int3;
				}
				else
				{
					array[Mathf.Abs(i - vec3Int.x), Mathf.Abs(j - vec3Int.z)] = Vec3Int.zero;
				}
			}
		}
		return FindLargestConnectedComponent(array, validPositions);
	}

	public Vec3Int[,] GetMeshAreaCrops(Vec3Int inputStart, Vec3Int inputEnd, PlantMapResource plantMapResource, Func<Vec3Int, PlantMapResource, bool, bool> condition)
	{
		Vec3Int vec3Int = Vec3Int.Min(in inputStart, in inputEnd);
		Vec3Int vec3Int2 = Vec3Int.Max(in inputStart, in inputEnd);
		int num = vec3Int2.x - vec3Int.x + 1;
		int num2 = vec3Int2.z - vec3Int.z + 1;
		int y = vec3Int.y / World.MapBlockHeight;
		Vec3Int[,] array = new Vec3Int[num, num2];
		for (int i = vec3Int.x; i <= vec3Int2.x; i++)
		{
			for (int j = vec3Int.z; j <= vec3Int2.z; j++)
			{
				Vec3Int vec3Int3 = new Vec3Int(i, y, j);
				if (condition(vec3Int3, plantMapResource, arg3: false))
				{
					array[Mathf.Abs(i - vec3Int.x), Mathf.Abs(vec3Int.z - j)] = vec3Int3;
				}
				else
				{
					array[Mathf.Abs(i - vec3Int.x), Mathf.Abs(j - vec3Int.z)] = Vec3Int.zero;
				}
			}
		}
		return FindLargestConnectedComponent(array);
	}

	public Vec3Int[,] GetModifiedCropsMeshArea(Vec3Int inputStart, Vec3Int inputEnd, IList<Vec3Int> validPositions, PlantMapResource plantMapResource, Func<Vec3Int, PlantMapResource, bool, bool> condition)
	{
		Vec3Int vec3Int = Vec3Int.Min(in inputStart, in inputEnd);
		Vec3Int vec3Int2 = Vec3Int.Max(in inputStart, in inputEnd);
		int num = vec3Int2.x - vec3Int.x + 1;
		int num2 = vec3Int2.z - vec3Int.z + 1;
		int y = vec3Int.y / World.MapBlockHeight;
		Vec3Int[,] array = new Vec3Int[num, num2];
		for (int i = vec3Int.x; i <= vec3Int2.x; i++)
		{
			for (int j = vec3Int.z; j <= vec3Int2.z; j++)
			{
				Vec3Int vec3Int3 = new Vec3Int(i, y, j);
				if (condition(vec3Int3, plantMapResource, arg3: true) && validPositions.Contains(vec3Int3))
				{
					array[Mathf.Abs(i - vec3Int.x), Mathf.Abs(vec3Int.z - j)] = vec3Int3;
				}
				else
				{
					array[Mathf.Abs(i - vec3Int.x), Mathf.Abs(j - vec3Int.z)] = Vec3Int.zero;
				}
			}
		}
		return FindLargestConnectedComponent(array, validPositions);
	}

	private Vec3Int[,] FindLargestConnectedComponent(Vec3Int[,] originalInput, IList<Vec3Int> validPositions = null)
	{
		Vec3Int[][] originalJaggedInput = ((Vec3Int[,])originalInput.Clone()).ConvertToJagged();
		Vec3Int[,] visited = new Vec3Int[originalInput.GetLength(0), originalInput.GetLength(1)];
		Vec3Int[,] result = new Vec3Int[originalInput.GetLength(0), originalInput.GetLength(1)];
		int count = 0;
		int l = originalJaggedInput.Length;
		int k = originalJaggedInput[0].Length;
		Vec3Int visitedKey = Vec3Int.up;
		ResetToZeroArray(visited);
		ResetToZeroArray(result);
		Vec3Int[,] finalResult = IterativeDfs();
		PrepareResults();
		return originalInput;
		Vec3Int[,] IterativeDfs()
		{
			Vec3Int[,] result2 = new Vec3Int[originalInput.GetLength(0), originalInput.GetLength(1)];
			for (int num = 0; num < originalJaggedInput.Length; num++)
			{
				for (int num2 = 0; num2 < originalJaggedInput[0].Length; num2++)
				{
					if (!(visited[num, num2] == visitedKey))
					{
						Stack<Vector2Int> stack = new Stack<Vector2Int>();
						stack.Push(new Vector2Int(num, num2));
						int num3 = 0;
						while (stack.Count > 0)
						{
							Vector2Int vector2Int = stack.Pop();
							int x = vector2Int.x;
							int y = vector2Int.y;
							if (Safe(originalJaggedInput, x, y))
							{
								visited[x, y] = visitedKey;
								result[x, y] = Vec3Int.one;
								stack.Push(new Vector2Int(x - 1, y));
								stack.Push(new Vector2Int(x + 1, y));
								stack.Push(new Vector2Int(x, y - 1));
								stack.Push(new Vector2Int(x, y + 1));
								num3++;
							}
						}
						if (num3 > count)
						{
							count = num3;
							result2 = (Vec3Int[,])result.Clone();
							ResetToZeroArray(result);
						}
					}
				}
			}
			return result2;
		}
		void PrepareResults()
		{
			for (int m = 0; m < l; m++)
			{
				for (int n = 0; n < k; n++)
				{
					ref Vec3Int reference = ref finalResult[m, n];
					Vec3Int rhs = Vec3Int.zero;
					if (reference == rhs)
					{
						originalInput[m, n] = Vec3Int.zero;
					}
				}
			}
		}
		void ResetToZeroArray(Vec3Int[,] source)
		{
			for (int num4 = 0; num4 < l; num4++)
			{
				for (int num5 = 0; num5 < k; num5++)
				{
					source[num4, num5] = Vec3Int.zero;
				}
			}
		}
		bool Safe(Vec3Int[][] grid, int i, int j)
		{
			if (i >= 0 && i < l && j >= 0 && j < k && visited[i, j] != visitedKey)
			{
				ref Vec3Int reference2 = ref grid[i][j];
				Vec3Int rhs2 = Vec3Int.zero;
				if (reference2 != rhs2)
				{
					if (validPositions != null)
					{
						return validPositions.Contains(originalInput[i, j]);
					}
					return true;
				}
			}
			return false;
		}
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
