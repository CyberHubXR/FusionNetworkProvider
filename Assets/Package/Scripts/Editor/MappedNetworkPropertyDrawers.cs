using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CyberHub.Brane;
using Fusion;
using UnityEditor;
using UnityEngine;

namespace Foundry.Networking.Fusion.Editor
{
    
    [CustomPropertyDrawer(typeof(NetworkTransformProperties))]
    public class MappedNetworkPropertyDrawers : PropertyDrawer
    {
        float propHeight = 0;
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            propHeight = base.GetPropertyHeight(property, label);
            return propHeight * 4;
        }
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            
            var props = this.GetSerializedValue<MappedProperties>(property);
            
            FusionNetTransformProps fp = props.GetProps<FusionNetTransformProps>("Fusion");

            var propPos = position;
            propPos.height = propHeight;
            
            EditorGUI.BeginChangeCheck();
            fp.interpolationDataSource = (NetworkBehaviour.InterpolationDataSources)EditorGUI.EnumPopup(propPos, "Interpolation Data Source", fp.interpolationDataSource);
            propPos.y += propHeight;
            
            fp.noInterpolationWhenOwned = EditorGUI.Toggle(propPos, new GUIContent("No Interpolation When Owned", "Easy way to automatically set interpolationDataSource to none when you have ownership to reduce local jitter"), fp.noInterpolationWhenOwned);
            propPos.y += propHeight;
            
            fp.interpolationSpace = (Spaces)EditorGUI.EnumPopup(propPos, "Interpolation Space", fp.interpolationSpace);
            propPos.y += propHeight;

            fp.isRigidbody = EditorGUI.Toggle(propPos, "Is Rigidbody", fp.isRigidbody);
            propPos.y += propHeight;
            

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(property.serializedObject.targetObject, "Fusion Properties");
                props.SetProps("Fusion", fp);
            }

            EditorGUI.EndProperty();
        }
    }
    
    
    public static class PropDrawExtensions
    {
        public static T GetSerializedValue<T>(this PropertyDrawer propertyDrawer, SerializedProperty property)
        {
            object @object = propertyDrawer.fieldInfo.GetValue(property.serializedObject.targetObject);

            // UnityEditor.PropertyDrawer.fieldInfo returns FieldInfo:
            // - about the array, if the serialized object of property is inside the array or list;
            // - about the object itself, if the object is not inside the array or list;

            // We need to handle both situations.
            if (@object.GetType().GetInterfaces().Contains(typeof(IList<T>)))
            {
                int propertyIndex = int.Parse(property.propertyPath[property.propertyPath.Length - 2].ToString());

                return ((IList<T>)@object)[propertyIndex];
            }
            
            return (T)@object;
        }
    }
}
