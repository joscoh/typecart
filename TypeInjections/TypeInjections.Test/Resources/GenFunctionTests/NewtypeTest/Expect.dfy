include "Old.dfy"
include "New.dfy"

  module Combine {

    import Old

    import New
    function fooOldToNew(f: Old.N.foo): (f': New.N.foo)
      ensures f as int == f' as int
      decreases f
    {
      f as int as New.N.foo
    }

    function barOldToNew(b: Old.N.bar): (b': New.N.bar)
      ensures b as real == b' as real
      decreases b
    {
      b as real as New.N.bar
    }
  }
