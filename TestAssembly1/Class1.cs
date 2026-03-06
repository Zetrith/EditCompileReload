namespace TestAssembly1;

public class Class1
{
    public static int b = 1;
    public int d;

    public int Prop { get; }

    public Class1()
    {
        d = 1;
    }

    public static object[] TestBasics()
    {
        return [
            (b, "Existing static field"),
            (new Class1().d, "Existing constructor"),
            (new Class1().Test2(), "Existing instance method"),
            (new Class1().Prop, "Existing property auto-getter"),
            (Class5.Test(), "Existing static method on nested type"),
            (new Struct().Test(), "Existing instance method on struct type"),
            (new Struct().Prop, "Existing property auto-getter on struct type"),
        ];
    }

    public static object[] TestGenerics()
    {
        Class3<int>.a = 1;
        int a = 1;
        new Class1().Test(ref a);

        return [
            (Class3<int>.a, "Existing static field on generic type"),
            (Test3<int>(), "Existing static generic method"),
            (Class3<int>.Test1(), "Existing static method on generic type"),
            (Class3<int>.Test2<int>(), "Existing static generic method on generic type"),
            (Class3<int>.Class4.Test(), "Existing static method on (nested type on generic type)"),
            (new Class1().Test2<int>(), "Existing instance generic method"),
            (new Class3<int>().Test3(), "Existing instance method on generic type"),
            (new Class3<int>().Test3<int>(), "Existing instance generic method on generic type"),
            (a, "Test existing instance generic method with by-ref parameter"),
            (new Struct3<int>().Prop, "Existing property auto-getter on generic struct type"),
        ];
    }

    public int Test2()
    {
        return 1;
    }

    public int Test2<T>()
    {
        return 1;
    }

    public void Test<T>(ref T? t)
    {
        t = default;
    }

    public static int Test3<U>()
    {
        return 1;
    }

    public static IEnumerable<T> AllNotNull<T>(IEnumerable<T> e)
    {
        return e.Where(t => t != null);
    }

    public class Class5
    {
        public static int Test()
        {
            return 1;
        }
    }
}

public class Class3<T>
{
    public static int a;

    public static int Test1()
    {
        return a;
    }

    public static int Test2<TU>()
    {
        return 1;
    }

    public int Test3()
    {
        return 1;
    }

    public int Test3<T>()
    {
        return 1;
    }

    public class Class4
    {
        public static int Test()
        {
            return 1;
        }
    }
}

public struct Struct
{
    public int Prop { get; }

    public int Test()
    {
        return 1;
    }
}

public struct Struct3<T>
{
    public int Prop { get; }
}
