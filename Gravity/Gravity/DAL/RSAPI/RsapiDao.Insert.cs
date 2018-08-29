﻿using kCura.Relativity.Client;
using kCura.Relativity.Client.DTOs;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Gravity.Base;
using Gravity.Exceptions;
using Gravity.Extensions;

namespace Gravity.DAL.RSAPI
{
	public partial class RsapiDao
	{
		#region RDO INSERT Protected Stuff
		protected int InsertRdo(RDO newRdo)
		{
			var resultArtifactId = rsapiProvider.CreateSingle(newRdo);

			if (resultArtifactId <= 0)
			{
				throw new ArgumentException("Was not able to insert new RDO with resultInt <= 0, with name " + newRdo.TextIdentifier);
			}

			return resultArtifactId;
		}

		protected void InsertUpdateFileFields(BaseDto objectToInsert, int parentId)
		{
			foreach (var propertyInfo in objectToInsert.GetType().GetProperties().Where(c => c.GetCustomAttribute<RelativityObjectFieldAttribute>()?.FieldType == RdoFieldType.File))
			{
				RelativityFile relativityFile = propertyInfo.GetValue(objectToInsert) as RelativityFile;
				InsertUpdateFileField(relativityFile, parentId);
			}
		}

		protected void InsertUpdateFileField(RelativityFile relativityFile, int parentId)
		{
			if (relativityFile?.FileValue == null)
			{
				return;
			}

			bool temporaryFile = false;
			var filePath = relativityFile.FileValue.Path;
			if (string.IsNullOrWhiteSpace(filePath))
			{
				filePath = string.IsNullOrWhiteSpace(relativityFile.FileMetadata.FileName)
					? throw new ArgumentException("No file path or name")
					: Path.GetTempPath() + relativityFile.FileMetadata.FileName;

				File.WriteAllBytes(filePath, relativityFile.FileValue.Data);

				temporaryFile = true;
			}

			try
			{ 
				rsapiProvider.UploadFile(relativityFile.ArtifactTypeId, parentId, filePath);
			}
			finally
			{
				if (temporaryFile)
				{
					invokeWithRetryService.InvokeVoidMethodWithRetry(() => File.Delete(filePath));
				}
			}
		}

		protected void InsertUpdateFileField(Guid fieldGuid, int objectArtifactId, FileDto fileDto)
		{
			var fileFieldArtifactId = rsapiProvider.Read(new RDO(fieldGuid)).GetResultData().Single().ArtifactID;

			if (fileDto == null)
				return;		//TODO: handle file deletion

			var currentMD5 = fileDto.GetCurrentMD5();
			if (currentMD5 == fileDto.LastOperationMD5)
				return;
			
			FilePathFileDto temporaryFileDto = null;
			if (fileDto is ByteArrayFileDto arrayFileDto)
			{
				//TODO: check file name not null or empty
				temporaryFileDto = arrayFileDto.WriteToFile(Path.Combine(Path.GetTempPath(), arrayFileDto.FileName));
			}

			try
			{
				var filePath = (temporaryFileDto ?? (FilePathFileDto)fileDto).FilePath;
				rsapiProvider.UploadFile(fileFieldArtifactId, objectArtifactId, filePath);
				fileDto.LastOperationMD5 = currentMD5;
			}
			finally
			{
				if (temporaryFileDto != null)
				{
					invokeWithRetryService.InvokeVoidMethodWithRetry(() => File.Delete(temporaryFileDto.FilePath));
				}
			}
		}


		private void InsertChildListObjectsWithDynamicType(BaseDto theObjectToInsert, int resultArtifactId, PropertyInfo propertyInfo)
		{
			var childObjectsList = propertyInfo.GetValue(theObjectToInsert, null) as IList;

			if (childObjectsList != null && childObjectsList?.Count != 0)
			{
				var childType = propertyInfo.PropertyType.GetEnumerableInnerType();
				this.InvokeGenericMethod(childType, nameof(InsertChildListObjects), childObjectsList, resultArtifactId);
			}
		}

		private static void SetParentArtifactID<T>(T objectToBeInserted, int parentArtifactId) where T : BaseDto
		{
			PropertyInfo parentArtifactIdProperty = objectToBeInserted.GetParentArtifactIdProperty();

			if (parentArtifactIdProperty == null)
			{
				return;
			}

			parentArtifactIdProperty.SetValue(objectToBeInserted, parentArtifactId);
			objectToBeInserted.ArtifactId = 0;
		}
		#endregion

		internal void InsertChildListObjects<T>(IList<T> objectsToInserted, int parentArtifactId)
			where T : BaseDto
		{
			var childObjectsInfo = BaseDto.GetRelativityObjectChildrenListProperties<T>();

			bool isFilePropertyPresent = typeof(T).GetProperties().ToList().Any(c => c.DeclaringType.IsAssignableFrom(typeof(RelativityFile)));

			if (childObjectsInfo.Any() || isFilePropertyPresent)
			{
				foreach (var objectToBeInserted in objectsToInserted)
				{
					SetParentArtifactID(objectToBeInserted, parentArtifactId);
					int insertedRdoArtifactID = InsertRdo(objectToBeInserted.ToRdo());
					InsertUpdateFileFields(objectToBeInserted, insertedRdoArtifactID);

					foreach (var childPropertyInfo in childObjectsInfo)
					{
						InsertChildListObjectsWithDynamicType(objectToBeInserted, insertedRdoArtifactID, childPropertyInfo);
					}
				}
			}
			else
			{

				foreach (var objectToBeInserted in objectsToInserted)
				{
					SetParentArtifactID(objectToBeInserted, parentArtifactId);
				}

				var rdosToBeInserted = objectsToInserted.Select(x => x.ToRdo()).ToArray();

				rsapiProvider.Create(rdosToBeInserted);
			}
		}

		private bool InsertSingleObjectFields(BaseDto objectToInsert)
		{
			foreach (var propertyInfo in objectToInsert.GetType().GetProperties())
			{
				var attribute = propertyInfo.GetCustomAttribute<RelativityObjectFieldAttribute>();
				if (attribute?.FieldType == RdoFieldType.SingleObject)
				{
					var fieldValue = (BaseDto)objectToInsert.GetPropertyValue(propertyInfo.Name);
					if (fieldValue != null)
					{
						Type objType = fieldValue.GetType();
						var newArtifactId = this.InvokeGenericMethod(objType, nameof(Insert), fieldValue);
						fieldValue.ArtifactId = (int)newArtifactId;
					}
				}
			}
			return true;
		}

		private bool InsertMultipleObjectFields(BaseDto objectToInsert)
		{
			foreach (var propertyInfo in objectToInsert.GetType().GetProperties().Where(c =>
				c.GetCustomAttribute<RelativityObjectFieldAttribute>()?.FieldType == RdoFieldType.MultipleObject))
			{
				var fieldGuid = propertyInfo.GetCustomAttribute<RelativityObjectFieldAttribute>()?.FieldGuid;
				if (fieldGuid == null)
				{
					continue;
				}

				IEnumerable<object> fieldValue = (IEnumerable<object>)objectToInsert.GetPropertyValue(propertyInfo.Name);
				if (fieldValue == null)
				{
					continue;
				}

				foreach (var childObject in fieldValue)
				{
					//TODO: better test to see if contains value...if all fields are null, not need
					if (((childObject as BaseDto).ArtifactId == 0))
					{
						Type objType = childObject.GetType();
						var newArtifactId = this.InvokeGenericMethod(objType, nameof(Insert), childObject);
						(childObject as BaseDto).ArtifactId = (int)newArtifactId;
					}
					else
					{
						//TODO: Consider update if fields have changed

					}
				}
			}
			return true;
		}

		public int Insert<T>(T theObjectToInsert) where T : BaseDto
		{
			//TODO: should think about some sort of transaction type around this.  If any parts of this fail, it should all fail
			InsertSingleObjectFields(theObjectToInsert);
			InsertMultipleObjectFields(theObjectToInsert);

			int resultArtifactId = InsertRdo(theObjectToInsert.ToRdo());

			InsertUpdateFileFields(theObjectToInsert, resultArtifactId);

			

			var childObjectsInfo = BaseDto.GetRelativityObjectChildrenListProperties<T>();
			foreach (var childPropertyInfo in childObjectsInfo)
			{
				InsertChildListObjectsWithDynamicType(theObjectToInsert, resultArtifactId, childPropertyInfo);
			}

			return resultArtifactId;
		}
	}
}
