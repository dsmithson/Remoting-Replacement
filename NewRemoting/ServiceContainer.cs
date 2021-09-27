﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NewRemoting
{
	/// <summary>
	/// Acts as a static service container, where an instance can be queried using a type.
	/// Only one instance can be added for a specific type.
	/// </summary>
	public sealed class ServiceContainer
	{
		private static Dictionary<Type, object> _serviceDictionary;

		static ServiceContainer()
		{
			_serviceDictionary = new Dictionary<Type, object>();
		}

		public static void AddService<T>(T instance)
		{
			AddService(typeof(T), instance);
		}

		public static void AddService(Type typeOfService, object instance)
		{
			_serviceDictionary.Add(typeOfService, instance);
		}

		public static T GetService<T>()
		{
			return (T)GetService(typeof(T));
		}

		public static object GetService(Type typeOfService)
		{
			if (_serviceDictionary.TryGetValue(typeOfService, out var instance))
			{
				return instance;
			}

			return null;
		}

		public static void RemoveService<T>()
		{
			_serviceDictionary.Remove(typeof(T));
		}

		public static void RemoveService(Type typeOfService)
		{
			_serviceDictionary.Remove(typeOfService);
		}
	}
}
