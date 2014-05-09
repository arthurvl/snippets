namespace Utilities
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Diagnostics.Contracts;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Runtime.Serialization;

    internal interface IContext<out TRoot>
    {
        T Match<T>(
            Func<T> onNull,
            Func<ILeft<Delegate>, IRight<object, object>, IContext<TRoot>, T> onCons);
    }

    internal interface ILeft<out TExpects>
    {
        TExpects Get();

        T Match<T>(
            Func<TExpects, T> onUnit,
            Func<ILeft<Delegate>, object, T> onCons);
    }

    internal interface IRight<out TParent, in TProvides>
    {
        TParent Get(TProvides values);

        T Match<T>(
            Func<T> onNull,
            Func<IRight<object, object>, object, T> onCons);
    }

    public static class ZipperHelpers
    {
        public static Zipper<TRoot> ZipValue<TRoot, TValue>(this Zipper<TRoot> zipper, Expression<Func<object, TValue>> fieldSelector, TValue value)
        {
            Contract.Requires(zipper != null);

            return zipper.MoveDownTo(fieldSelector).Transform(obj => value).MoveUp();
        }

        public static Zipper<TRoot, THole> ZipValue<TRoot, THole, TValue>(this Zipper<TRoot, THole> zipper, Expression<Func<THole, TValue>> fieldSelector, TValue value)
        {
            Contract.Requires(zipper != null);

            return zipper.MoveDownTo(fieldSelector).Transform(obj => value).MoveUp<THole>();
        }

        public static Zipper<TRoot, THole> MoveDownTo<TRoot, THole>(this Zipper<TRoot> zipper, Expression<Func<object, THole>> selector)
        {
            Contract.Requires(zipper != null);

            return (new Zipper<TRoot, object>(zipper)).MoveDownTo(selector);
        }

        public static Zipper<TRoot, TOtherHole> MoveSidewaysTo<TRoot, TParent, TOtherHole, THole>(
            this Zipper<TRoot, THole> zipper,
            Expression<Func<TParent, TOtherHole>> selector)
        {
            Contract.Requires(zipper != null);

            return zipper.MoveUp<TParent>().MoveDownTo(selector);
        }

        public static Zipper<T, T> ToTypedZipper<T>(this T obj)
        {
            return Zipper<T, T>.ToZipper(obj);
        }

        public static Zipper<T> ToZipper<T>(this T obj)
        {
            return Zipper<T>.ToZipper(obj);
        }
    }

    // modeled after http://michaeldadams.org/papers/scrap_your_zippers/ScrapYourZippers-2010.pdf
    // Uses type erasure, explicit casts and reflection to get C# to cooperate
    public sealed class Zipper<TRoot>
    {
        private readonly IContext<TRoot> context;

        private readonly object zipperhole;

        private Zipper(object hole, IContext<TRoot> context)
        {
            this.zipperhole = hole;
            this.context = context;
        }

        public static Zipper<TRoot> ToZipper(TRoot value)
        {
            return new Zipper<TRoot>(value, Context<TRoot>.Null());
        }

        public TRoot FromZipper()
        {
            return (TRoot)this.context.Match(
                () => this.zipperhole,
                (lefts, rights, cont) =>
                new Zipper<TRoot>(Context<TRoot>.Combine(lefts, this.zipperhole, rights), cont).FromZipper());
        }

        public Zipper<TRoot> MoveDown()
        {
            ILeft<object> left = ToLeft(this.zipperhole);
            return left.Match(OnUnitLeftMoveDown, OnConsLeftMoveDown);
        }

        public Zipper<TRoot> MoveLeft()
        {
            return this.context.Match(
                () => null,
                (left, right, cont) =>
                left.Match(
                    _ => null,
                    (l, value) =>
                    {
                        var newCons =
                            Context<TRoot>.Cons(
                                l,
                                Right<object, object>.Cons(right, this.zipperhole),
                                cont); // Cast should be safe by construction

                        return new Zipper<TRoot>(value, newCons);
                    }));
        }

        public Zipper<TRoot> MoveRight()
        {
            return this.context.Match(
                () => null,
                (left, right, cont) =>
                right.Match(
                    () => null,
                    (r, value) =>
                    {
                        var newCons =
                            Context<TRoot>.Cons(
                                Left.Cons(left, this.zipperhole),
                                r,
                                cont); // Cast should be safe by construction

                        return new Zipper<TRoot>(value, newCons);
                    }));
        }

        public Zipper<TRoot> MoveUp()
        {
            return this.context.Match(
                () => null,
                (left, right, cont) =>
                new Zipper<TRoot>(
                    Context<TRoot>.Combine(left, this.zipperhole, right),
                    cont));
        }

        public TV Query<TV>(Func<object, TV> query)
        {
            Contract.Requires(query != null);

            return query(this.zipperhole);
        }

        public Zipper<TRoot> Transform(Func<object, object> transform)
        {
            Contract.Requires(transform != null);

            var t = transform(this.zipperhole);
            var z = new Zipper<TRoot>(t, this.context);
            return z;
        }

        // NOTE: This method is only used using reflection from GetFakeObjectConstructor
        private static object CreateAndFill(object original, Type holeType, FieldInfo[] fields, object[] fieldValues)
        {
            // First check if we need to create a new object, which is the case if any of the fieldvalues
            // is not referenceequal to those of the fields in the original

            var originalFieldValues = fields.Select(field => field.GetValue(original));

            var referenceEqualities = originalFieldValues.Zip(fieldValues, ReferenceEquals);

            if (referenceEqualities.All(equal => equal))
            {
                return original;
            }

            object result = FormatterServices.GetUninitializedObject(holeType);

            // Passing a FieldInfo[] instead of MemberInfo[] is safe here, as it is only read, never written to
            FormatterServices.PopulateObjectMembers(result, fields, fieldValues);

            return result;
        }

        private static Func<object, Func<object, object>> EnumerableCons(object original, object originalhead, object originaltail)
        {
            return head => tail =>
                ReferenceEquals(head, originalhead) && ReferenceEquals(tail, originaltail)
                    ? original
                    : new ConsEnumerable(head, (IEnumerable)tail);
        }

        private static Func<object, Func<object, object>> EnumerableCons(Type t, object original, object originalhead, object originaltail)
        {
            return head => tail =>
            {
                if (ReferenceEquals(head, originalhead) && ReferenceEquals(tail, originaltail))
                {
                    return original;
                }

                var castToCorrectType =
                    typeof(Enumerable)
                        .GetMethod("Cast", new[] { typeof(IEnumerable) })
                        .MakeGenericMethod(t);
                var result = new ConsEnumerable(head, (IEnumerable)tail);
                return castToCorrectType.Invoke(null, new object[] { result });
            };
        }

        private static Type GetEnumerableType(Type type)
        {
            Contract.Requires(type != null);

            return type.GetInterfaces()
               .Where(interfaceType => interfaceType.IsGenericType && interfaceType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
               .Select(interfaceType => interfaceType.GetGenericArguments()[0])
               .FirstOrDefault();
        }

        /// <summary>
        /// Makes a curried object constructor for an object
        /// </summary>
        /// <param name="original">The original object that we want to create a possibly modified copy of</param>
        /// <param name="type">The type of the object to construct</param>
        /// <param name="fields">The fields that need to be filled on the object. This must be a FieldInfo[] as it will be passed to FormatterServices.PopulateObjectMembers</param>
        /// <returns></returns>
        private static LambdaExpression GetFakeObjectConstructor(object original, Type type, FieldInfo[] fields)
        {
            Contract.Requires(fields != null);

            // assuming our type has properties  A,B,C,D
            // the fake constructor is (a) => (b) => (c) => (d) => CreateAndFill(holeType,properties,new object[] {a,b,c,d})
            IEnumerable<ParameterExpression> parameters = fields
                .Select(field => Expression.Parameter(field.FieldType, field.Name))
                .ToList(); // make sure the Expression.Parameter calls get evaluated!

            IEnumerable<Expression> castedParameters = parameters
                .Select(parameter => Expression.Convert(parameter, typeof(object)));

            Expression collectedParameterArray = Expression.NewArrayInit(typeof(object), castedParameters);

            MethodInfo create = typeof(Zipper<object>).GetMethod("CreateAndFill", BindingFlags.Static | BindingFlags.NonPublic);

            Expression createCall = Expression.Call(
                create,
                Expression.Constant(original),
                Expression.Constant(type),
                Expression.Constant(fields),
                collectedParameterArray);

            var fakeConstructor = (LambdaExpression)parameters // A B C D
                                                        .Reverse() // D C B A
                                                        .Aggregate(
                // (((create <= D) <= C) <= B) <= A
                                                            createCall,
                                                            (body, parameter) => Expression.Lambda(body, parameter));

            return fakeConstructor;
        }

        private static ILeft<object> ToLeft(object hole)
        {
            Contract.Requires(hole != null);

            Type holeType = hole.GetType();

            if (holeType.IsPrimitive || holeType == typeof(string))
            {
                // Primitives cannot be deconstructed, but autoboxed primitives *can* be,
                // so we need to specialcase them.
                return Left.Unit(null);
            }

            if (typeof(IEnumerable).IsAssignableFrom(holeType) && holeType != typeof(string))
            {
                return ToLeftForEnumerable((IEnumerable)hole);
            }

            return ToLeftForObject(hole);
        }

        private static ILeft<object> ToLeftForEnumerable(IEnumerable hole)
        {
            Contract.Requires(hole != null);

            // We assume every IEnumerable behaves as an infinite list and deconstruct
            // it as if it were either a cons of an element and an IEnumerable or
            // a nil; in the latter case we can not move down.
            var enumerator = hole.GetEnumerator();

            if (enumerator.MoveNext())
            {
                // cons
                var head = enumerator.Current;
                var tail = new EnumerableForEnumerator(enumerator);

                // We need to return an IEnumerable<T> if we have been passed
                // an IEnumerable<T>, so we need to check if that is the case
                // and then cast to the appropriate IEnumerable<T>
                var holeType = hole.GetType();
                var elementType = GetEnumerableType(holeType);

                return
                    Left.Cons(
                        Left.Cons(
                            Left.Unit(
                                elementType == null
                                    ? EnumerableCons(hole, head, tail)
                                    : EnumerableCons(elementType, hole, head, tail)),
                            head),
                        tail);
            }
            // nil, makes MoveDown return null, so delegate will never get called
            // so null is a safe argument to Unit (as we are only called from toLeft,
            // which transforms Left.Unit into null)
            return Left.Unit(null);
        }

        private static ILeft<object> ToLeftForObject(object hole)
        {
            //// Pseudocode:
            ////  create curried fake-constructor
            ////  put fake-constructor under Left.Unit
            ////  foreach(field in instance fields):
            ////      apply curried constructor to value through Left.Cons
            ////  return curried application

            Type holeType = hole.GetType();

            // We can only repopulate using the type's fields. As we stay
            // within the AppDomain we can safely copy references to other
            // objects. This behavior may break singleton-promises though,
            // so don't put singletons in a zipper, mkay?
            FieldInfo[] fields = holeType.GetInstanceFields().ToArray();

            ILeft<Delegate> unit = Left.Unit(
                GetFakeObjectConstructor(hole, holeType, fields).Compile());

            ILeft<Delegate> consed = fields.Aggregate(
                unit,
                (left, field) => Left.Cons(
                    left,
                    // Cast should be safe by construction
                    field.GetValue(hole)));

            return consed;
        }

        private Zipper<TRoot> OnConsLeftMoveDown(ILeft<Delegate> l, object value)
        {
            var newContext =
                Context<TRoot>.Cons(
                    l,
                    Right<object, object>.Null(),
                    this.context);

            return new Zipper<TRoot>(value, newContext);
        }

        private Zipper<TRoot> OnUnitLeftMoveDown(object hole)
        {
            return null;
        }

        private class ConsEnumerable : IEnumerable
        {
            private readonly object head;
            private readonly IEnumerable tail;

            internal ConsEnumerable(object head, IEnumerable tail)
            {
                Contract.Ensures(ReferenceEquals(tail, this.tail));
                Contract.Ensures(head == this.head);

                this.head = head;
                this.tail = tail;
            }

            public IEnumerator GetEnumerator()
            {
                Contract.Ensures(this.tail != null);

                return new ConsEnumerator(this.head, this.tail.GetEnumerator());
            }

            private class ConsEnumerator : IEnumerator
            {
                private readonly object head;
                private Location location = Location.Initial;
                private readonly IEnumerator tail;

                internal ConsEnumerator(object head, IEnumerator tail)
                {
                    Contract.Ensures(this.tail == tail);
                    Contract.Ensures(this.head == head);

                    this.head = head;
                    this.tail = tail;
                }

                private enum Location
                {
                    Initial,
                    AtHead,
                    InTail,
                    AtEnd
                }

                public object Current
                {
                    get
                    {
                        switch (this.location)
                        {
                            case Location.AtHead:
                                return this.head;
                            case Location.InTail:
                                return this.tail.Current;
                            default:
                                throw new InvalidOperationException("ConsEnumerator is at initial or final position");
                        }
                    }
                }

                public bool MoveNext()
                {
                    switch (this.location)
                    {
                        case Location.Initial:
                            this.location = Location.AtHead;
                            return true;

                        case Location.AtHead:
                            if (this.tail.MoveNext())
                            {
                                this.location = Location.InTail;
                                return true;
                            }

                            this.location = Location.AtEnd;
                            return false;

                        case Location.InTail:
                            if (this.tail.MoveNext())
                            {
                                return true;
                            }

                            this.location = Location.AtEnd;
                            return false;

                        default:
                            return false;
                    }
                }

                public void Reset()
                {
                    throw new NotSupportedException("Can only walk through ConsEnumerator in one direction");
                }
            }
        }

        private class EnumerableForEnumerator : IEnumerable
        {
            private readonly IEnumerator enumerator;

            internal EnumerableForEnumerator(IEnumerator enumerator)
            {
                Contract.Ensures(this.enumerator == enumerator);

                this.enumerator = enumerator;
            }

            public IEnumerator GetEnumerator()
            {
                Contract.Assume(this.enumerator != null);

                return this.enumerator;
            }
        }
    }

    public sealed class Zipper<TRoot, THole>
    {
        private readonly Zipper<TRoot> zipper;

        internal Zipper(Zipper<TRoot> from)
        {
            this.zipper = from;
        }

        public static Zipper<TRoot, TRoot> ToZipper(TRoot value)
        {
            return new Zipper<TRoot, TRoot>(value.ToZipper());
        }

        public TRoot FromZipper()
        {
            return this.zipper.FromZipper();
        }

        public Zipper<TRoot, TOtherHole> MoveDownTo<TOtherHole>(Expression<Func<THole, TOtherHole>> selector)
        {
            var fields = this.zipper
                .Query(obj => obj)
                .GetType()
                .GetInstanceFields()
                .ToArray();

            MemberInfo info = MemberHelper.GetSelectedMember(selector);
            var propertyInfo = info as PropertyInfo;
            if (propertyInfo != null)
            {
                info = MemberHelper.GetBackingField(propertyInfo);
            }

            var leftCount = fields.Length - 1 - Array.IndexOf(fields, info);

            var newZipper = this.zipper.MoveDown(); // Should succeed, as otherwise selector can not be statically valid

            for (int i = 0; i < leftCount; i++)
            {
                // Will succeed, as 0 < leftCount <= fields.Length
                // if leftCount == fields.Length then newZipper becomes null on last MoveLeft()
                // but this should never happen if selector is statically valid
                newZipper = newZipper.MoveLeft();
            }

            return new Zipper<TRoot, TOtherHole>(newZipper);
        }

        public Zipper<TRoot> MoveUp()
        {
            return this.zipper.MoveUp();
        }

        public Zipper<TRoot, TParent> MoveUp<TParent>()
        {
            var newZipper = this.zipper.MoveUp();
            if (newZipper.Query(o => o) is TParent)
            {
                return new Zipper<TRoot, TParent>(newZipper);
            }

            return null;
        }

        public THole Query()
        {
            return this.zipper.Query(obj => (THole)obj);
        }

        public Zipper<TRoot, TOtherHole> Transform<TOtherHole>(Func<THole, TOtherHole> transform)
        {
            return new Zipper<TRoot, TOtherHole>(this.zipper.Transform(from => (object)transform((THole)from)));
        }
    }

    internal static class Left
    {
        public static ILeft<Delegate> Cons<THole>(
            ILeft<Delegate> partialConstructor,
            THole value)
        {
            return new ConsLeft<THole, Delegate>(partialConstructor, value);
        }

        public static ILeft<Func<TA, TB>> Unit<TA, TB>(Func<TA, TB> o)
        {
            return new UnitLeft<Func<TA, TB>>(o);
        }

        public static ILeft<Delegate> Unit(Delegate o)
        {
            return new UnitLeft<Delegate>(o);
        }

        private sealed class ConsLeft<THole, TExpected> : ILeft<TExpected>
        {
            private readonly ILeft<Delegate> partialConstructor;

            private readonly THole value;

            public ConsLeft(ILeft<Delegate> partialConstructor, THole value)
            {
                this.partialConstructor = partialConstructor;
                this.value = value;
            }

            public TExpected Get()
            {
                Contract.Assume(this.partialConstructor != null);

                var cons = this.partialConstructor.Get();
                var result = cons.DynamicInvoke(this.value);
                return (TExpected)result;
            }

            public T Match<T>(Func<TExpected, T> onUnit, Func<ILeft<Delegate>, object, T> onCons)
            {
                Contract.Assume(onCons != null);

                return onCons(this.partialConstructor, this.value);
            }
        }

        private sealed class UnitLeft<TExpected> : ILeft<TExpected>
        {
            private readonly TExpected emptyConstructor;

            public UnitLeft(TExpected emptyConstructor)
            {
                this.emptyConstructor = emptyConstructor;
            }

            public TExpected Get()
            {
                return this.emptyConstructor;
            }

            public T Match<T>(Func<TExpected, T> onUnit, Func<ILeft<Delegate>, object, T> onCons)
            {
                Contract.Assume(onUnit != null);

                return onUnit(this.emptyConstructor);
            }
        }
    }

    internal static class ReflectionHelpers
    {
        public static IEnumerable<FieldInfo> GetInstanceFields(this Type type)
        {
            Contract.Requires(type != null);
            Contract.Ensures(Contract.Result<FieldInfo>() != null);

            return type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .OrderBy(field => field.MetadataToken);
        }
    }

    internal static class Right<TParent, TProvides>
    {
        public static IRight<TParent, TProvides> Cons<THole, TA, TT>(IRight<TT, TA> partialConstructor, THole value)
        {
            return new ConsRight<TParent, TProvides, THole, TA, TT>(partialConstructor, value);
        }

        public static IRight<TParent, TParent> Null()
        {
            return new NullRight<TParent>();
        }

        private sealed class ConsRight<TPar, TProv, THole, TAny, T> : IRight<TPar, TProv> // where TProv : Func<THole,TAny>
        {
            private readonly IRight<T, TAny> partialConstructor;

            private readonly THole value;

            public ConsRight(IRight<T, TAny> partialConstructor, THole value)
            {
                Contract.Requires(partialConstructor != null);

                this.partialConstructor = partialConstructor;
                this.value = value;
            }

            public TPar Get(TProv provided)
            {
                return (TPar)(object)this.partialConstructor.Get((TAny)((Delegate)(object)provided).DynamicInvoke(this.value));
                ////   ^^^^^^^^^^^^^   - typechecker fails -     ^^^^^^^^^^^^^^^^^^^^^^
                ////   type t in IRight<t,a> PartialConstructor unifies with type par
                ////   and prov with Func<hole,a>
                ////   but the C# typechecker is not smart enough to figure that out
            }

            public TResult Match<TResult>(Func<TResult> onNull, Func<IRight<object, object>, object, TResult> onCons)
            {
                Contract.Assume(onCons != null);

                return onCons((IRight<object, object>)this.partialConstructor, this.value);
            }
        }

        private sealed class NullRight<TPar> : IRight<TPar, TPar>
        {
            public TPar Get(TPar provided)
            {
                return provided;
            }

            public T Match<T>(Func<T> onNull, Func<IRight<object, object>, object, T> onCons)
            {
                Contract.Assume(onNull != null);

                return onNull.Invoke();
            }
        }
    }

    internal class Context<TRoot>
    {
        public static TParent Combine<TRights, TParent>(
            ILeft<Delegate> lefts,
            object hole,
            IRight<TParent, TRights> rights)
        {
            Contract.Requires(lefts != null);
            Contract.Requires(rights != null);

            return rights.Get((TRights)lefts.Get().DynamicInvoke(hole));
        }

        public static IContext<TRoot> Cons<TRights, TParent>(
            ILeft<Delegate> left,
            IRight<TParent, TRights> right,
            IContext<TRoot> context)
        {
            return new ConsContext<TRoot, TRights, TParent>(left, right, context);
        }

        public static IContext<TRoot> Null()
        {
            return new NullContext<TRoot>();
        }

        private sealed class ConsContext<TR, TRights, TParent> : IContext<TR>
        {
            private readonly IContext<TR> context;

            private readonly ILeft<Delegate> left;

            private readonly IRight<TParent, TRights> right;

            public ConsContext(ILeft<Delegate> left, IRight<TParent, TRights> right, IContext<TR> context)
            {
                Contract.Ensures(this.right == right);

                this.left = left;
                this.right = right;
                this.context = context;
            }

            public T Match<T>(Func<T> onNull, Func<ILeft<Delegate>, IRight<object, object>, IContext<TR>, T> onCons)
            {
                Contract.Assume(onCons != null);

                var l = this.left;
                var r = (IRight<object, object>)this.right;
                var c = this.context;
                return onCons(l, r, c);
            }
        }

        private sealed class NullContext<TR> : IContext<TR>
        {
            public T Match<T>(Func<T> onNull, Func<ILeft<Delegate>, IRight<object, object>, IContext<TR>, T> onCons)
            {
                Contract.Assume(onNull != null);

                return onNull.Invoke();
            }
        }
    }
}
