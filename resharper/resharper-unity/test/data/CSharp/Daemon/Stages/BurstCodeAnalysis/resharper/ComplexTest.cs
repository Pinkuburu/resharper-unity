using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

#pragma warning disable 168
#pragma warning disable 162
#pragma warning disable 219
#pragma warning disable 414
#pragma warning disable 1717

namespace Unity
{
    namespace Jobs
    {
        public interface IJob
        {
            void Execute();
        }
    }

    namespace Burst
    {
        public class BurstCompileAttribute : Attribute
        {
        }

    }

    namespace Collections
    {
        public struct NativeArray<T> : IDisposable, IEnumerable<T>, IEnumerable, IEquatable<NativeArray<T>>
            where T : struct
        {
            public void Dispose()
            {
                throw new NotImplementedException();
            }

            public IEnumerator<T> GetEnumerator()
            {
                throw new NotImplementedException();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            public bool Equals(NativeArray<T> other)
            {
                throw new NotImplementedException();
            }
        }
    }
}

public class NewBehaviourScript
{
    [BurstCompile]
    struct PrimitiveTest : IJob
    {
        // public char ch;//CGTD for struct to have characters as field they must declared CharSet=CharSetUnicode StructLayout. direct exception next. not supported currently.
        /*
         (0,0): Burst error BC1066: Unsupported parameter `ref NewBehaviourScript.PrimitiveTest` `data` in function `Unity.Jobs.IJobExtensions.JobStruct`1.Execute(ref T data, System.IntPtr additionalPtr, System.IntPtr bufferRangePatchData, ref Unity.Jobs.LowLevel.Unsafe.JobRanges ranges, int jobIndex)`: structs with characters that do not have the 'CharSet=CharSet.Unicode' StructLayout are not supported for external functions
        
        While compiling job: System.Void Unity.Jobs.IJobExtensions/JobStruct`1<NewBehaviourScript/PrimitiveTest>::Execute(T&,System.IntPtr,System.IntPtr,Unity.Jobs.LowLevel.Unsafe.JobRanges&,System.Int32)
        at <empty>:line 0
         */
        // public int kek;
        public void MustBeProhibited()
        {
            string str2 = "asdasd"; //Burst error BC1033: Loading a managed string literal is not supported
            string str1 = null; //null is ok, it can be on stack
            str1 = str2; //CGTD may be some day it will become warning
            //CGTD it's different error, but some day...
            char
                c = str2[0]; //Burst error BC1016: The managed function `System.String.get_Chars(System.String* this, int index)` is not supported
            var ch = 'a';
            char ch2 = 'b';
            ch2 = ch;
            ch = 'd';
            char ch3 = new char();
            // var decel = new decimal();//CGTD dont care bout decimal, cuz it has extern methods. someday...
            // decel = 12m;
            // int kek = (int) decel;
        }

        public void Execute()
        {
            var varInt = 1;
            int intInt = 1;
            var newInt = new int();
            newInt = 1;
            MustBeProhibited();
        }
    }

    [BurstCompile]
    struct ExceptionsText : IJob
    {
        public void Execute()
        {
            F();
        }

        private void F()
        {
            throw new ArgumentException("kek");
            new ArgumentException(
                nameof(F)); //Burst error BC1021: Creating a managed object `here placed object ref' is not supported
            try //Burst error BC1005: The `try` construction is not supported
            {
                int a = 1;
            }
            catch (Exception e) //Burst error BC1037: The `catch` construction (e.g `foreach`/`using`) is not supported - only if try is ok but not empty. show only in pair with catch or finally
            {
                int b = 2;
            }
            finally //Burst error BC1036: The `finally` construction (e.g `foreach`/`using`) is not supported - only if try is ok but not empty. show only in pair with catch or finally
            {
                int c = 2;
            }
        }
    }

    [BurstCompile]
    struct FunctionParametersReturnValueTest : IJob
    {
        public interface IKek
        {
            void kek();
        }

        public struct Lol : IKek
        {
            public void kek()
            {
            }
        }

        public void Fobject(object a)
        {
        }

        public void Fkek(IKek kek)
        {
        }

        public void Flol(Lol lol)
        {
        }

        public IKek FReturn()
        {
            return new Lol();
        }

        public void GenericF<T>(T a) where T : struct, IKek
        {
            a.kek();
        }

        public void Execute()
        {
            Fobject(null); //Burst error BC1016: The managed function `NewBehaviourScript.FunctionParametersReturnValueTest.Fobject(NewBehaviourScript.FunctionParametersReturnValueTest* this, object a)` is not supported
            Fkek(null); //-+-
            Flol(new Lol());
            FReturn(); //-+-
            GenericF(new Lol());
        }
    }

    [BurstCompile]
    struct ForeachTest : IJob
    {
        public void Execute()
        {
            foreach (var kek in new NativeArray<int>()) //Burst error BC1037: The `try` construction (e.g `foreach`/`using`) is not supported - only if try is ok but not empty. show only in pair with catch or finally
            {
                Console.WriteLine(kek);
            }
        }
    }

    public static SimpleClass myClasss = new SimpleClass();

    [BurstCompile]
    struct MethodsInvocationTest : IJob
    {
        public void Execute()
        {
            F();
        }

        public override int GetHashCode()
        {
            //CGTD boxing is very hard
            return
                base.GetHashCode(); // Burst error BC1020: Boxing a valuetype `NewBehaviourScript.MethodsInvocationTest` to a managed object is not supported
        }

        private void F()
        {
            SimpleClass.StaticMethod();
            GetType(); //Burst error BC1001: Unable to access the managed method `object.GetType()` from type `object`
            Equals(null,
                null); //Burst error BC1001: Unable to access the managed method `object.Equals(object)` from type `NewBehaviourScript.MethodsInvocationTest`
            Equals(null); //Burst error BC1001: Unable to access the managed method `object.Equals(object)` from type `NewBehaviourScript.MethodsInvocationTest`
            ToString(); //Unable to access the managed method `object.ToString()` from type `NewBehaviourScript.MethodsInvocationTest`
            GetHashCode(); // Burst error BC1001: Unable to access the managed method `object.GetHashCode()` from type `NewBehaviourScript.MethodsInvocationTest`
            var
                kek = myClasss; //Burst error BC1042: The managed class type `NewBehaviourScript/SimpleClass` is not supported. Loading from a non-readonly static field `NewBehaviourScript.myClasss` is not supported
            myClasss.PlainMethod(); //Burst error BC1042: The managed class type `NewBehaviourScript/SimpleClass` is not supported. Loading from a non-readonly static field `NewBehaviourScript.myClasss` is not supported
        }
    }

    private enum MyEnum
    {
        enumElem1,
        enumElem2
    }

    [BurstCompile]
    struct ReferenceExpressionTest : IJob
    {
        private static int getSetProp { get; set; }
        private static int field1 = 2;
        private readonly static int field2 = 2;
        private const int field3 = 2;

        private static int getProp { get; }

        //CGTD ReferenceExpressionTest.myClass is not a value type. Job structs may not contain any reference types. Also there is problem with transitive class usage in structs.
        //private SimpleClass myClass;
        private MyEnum ourEnum;

        public void Execute()
        {
            SimpleClass
                myClass =
                    new SimpleClass(); //Burst error BC1021: Creating a managed object `here placed object ref' is not supported

            getSetProp =
                2; //Burst error BC1034: Writing to a static field `NewBehaviourScript.ReferenceExpressionTest.<getSetProp>k__BackingField` is not supported.
            field1 = 2; //Burst error BC1034: Writing to a static field `fullname till field` is not supported

            ourEnum = ourEnum;
        }
    }

    public class SimpleClass
    {
        public static void StaticMethod()
        {
        }

        public void PlainMethod()
        {
        }
    }
}