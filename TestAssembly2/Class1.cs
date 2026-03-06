namespace TestAssembly1;

public class Class1
{
    public static int b = 2;
    public static int c_New = 2;
    public static int e_New = b;
    public int d;

    public int Prop { get; }

    public Class1()
    {
        d = 2;
    }

    // Existing static method
    public static object[] TestBasics()
    {
        return [
            (b, "Existing static field"),
            (c_New, "New static field (they currently take on the default value, 0)"),
            (e_New, "New static field"),
            (Class2_New.a_New, "New field on new type"),
            (new Class1().d, "Existing constructor"),
            (new Class1().Test2(), "Existing instance method"),
            (new Class1().Test2_New(), "New instance method"),
            (new Class1().Prop, "Existing property auto-getter"),
            (Class5.Test(), "Existing static method on nested type"),
            (Class5.Test_New(), "New static method on nested type"),
            (Struct2_New.Test(), "New static method on by-ref type"),
            (new Struct().Test(), "Existing instance method on struct type"),
            (new Struct().Prop, "Existing property auto-getter on struct type"),
        ];
    }

    public static object[] TestGenerics()
    {
        Class3<int>.b_New = 2;

        return [
            (Class3<int>.a, "Existing static field on generic type"),
            (Test3(1), "Existing static generic method"),
            (Class3<int>.Test1(1), "Existing static method on generic type"),
            (Class3<int>.Test2<int>(), "Existing static generic method on generic type"),
            (Class3<int>.Class4.Test(), "Existing static method on (nested type on generic type)"),
            (new Class1().Test2<int>(), "Existing instance generic method"),
            (new Class3<int>().Test3(), "Existing instance method on generic type"),
            (new Class3<int>().Test3<int>(), "Existing instance generic method on generic type"),
            (new Struct3<int>().Prop, "Existing property auto-getter on generic struct type"),
            (AllNotNull(["", null]).ToList(), "Existing static generic method with lambda"),
            (Class3<int>.b_New, "New static field on generic type"),
            (Test3_New<int>(), "New static generic method"),
            (Class3<int>.Test1_New(), "New static method on generic type"),
            (Class3<int>.Test2_New<int>(), "New static generic method on generic type"),
            (Class3<int>.Class4.Test_New(), "New static method on (nested type on generic type)"),
            (new Class1().Test2_New<int>(), "New instance generic method"),
            (new Class3<int>().Test3_New(), "New instance method on generic type"),
            (new Class3<int>().Test3_New<int>(), "New instance generic method on generic type"),
        ];
    }

    public int Test2()
    {
        return 2;
    }

    public int Test2<T>()
    {
        return 2;
    }

    public int Test2_New<T>()
    {
        return 2;
    }

    public int Test2_New()
    {
        return 2;
    }

    public static int Test3<T>(T t)
    {
        return 2;
    }

    public static int Test3_New<T>()
    {
        return 2;
    }

    public static IEnumerable<T> AllNotNull<T>(IEnumerable<T> e)
    {
        return e.Where(t => t != null);
    }

    public class Class5
    {
        public static int Test()
        {
            return 2;
        }

        public static int Test_New()
        {
            return 2;
        }
    }
}

public class Class3<T>
{
    public static int a;
    public static int b_New;

    public static int Test1(T t)
    {
        return b_New;
    }

    public static int Test1_New()
    {
        return b_New;
    }

    public static int Test2<TU>()
    {
        return 2;
    }

    public static int Test2_New<TU>()
    {
        return 2;
    }

    public int Test3()
    {
        return 2;
    }

    public int Test3<T>()
    {
        return 2;
    }

    public int Test3_New()
    {
        return 2;
    }

    public int Test3_New<T>()
    {
        return 2;
    }

    public class Class4
    {
        public static int Test()
        {
            return 2;
        }

        public static int Test_New()
        {
            return 2;
        }
    }
}

public class Class2_New
{
    public static int a_New = 2;

    public static int P_New { get; set; }

    public Class2_New(int a)
    {
    }

    public class Class5_New
    {
        public static int Test12_New()
        {
            return 1;
        }
    }
}

ref struct Struct2_New
{
    public static int Test()
    {
        return 2;
    }
}

public struct Struct
{
    public int Prop { get; }

    public int Test()
    {
        return 2;
    }
}

public struct Struct3<T>
{
    public int Prop { get; }
}
