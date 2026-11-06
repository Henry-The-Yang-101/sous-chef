"""The audit independently preserves quantities and native processing boundaries."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
from assembly_sweep import recipe_parts


def ingredient(ident):
    return {'type': 'IngredientAssembledNode', 'fields': {
        'IngredientAssembledNode.m_ingriedientOrderNode': {
            'fields': {'OrderDefinitionNode.m_uID': ident}}}}


def composite(kind, children, progress=None, step=7):
    data = {'CompositeAssembledNode.m_composition': children,
            'CompositeAssembledNode.m_optional': []}
    if progress:
        data[kind + '.m_progress'] = progress
    if kind == 'CookedCompositeAssembledNode':
        data[kind + '.m_cookingStep'] = {'fields': {'CookingStepData.m_uID': step}}
    return {'type': kind, 'fields': data}


class AssemblySweepTests(unittest.TestCase):
    def test_plain_wrappers_flatten_but_duplicate_quantities_remain(self):
        node = composite('CompositeAssembledNode', [ingredient(1), composite('CompositeAssembledNode', [ingredient(1)])])
        self.assertEqual(recipe_parts(node, True), [('ingredient', 'i1'), ('ingredient', 'i1')])

    def test_raw_and_cooked_are_not_equivalent(self):
        raw = ingredient(1)
        cooked = composite('CookedCompositeAssembledNode', [raw], 'Cooked')
        self.assertNotEqual(recipe_parts(raw, True), recipe_parts(cooked, True))
        self.assertNotEqual(recipe_parts(cooked, True), recipe_parts(composite('CookedCompositeAssembledNode', [raw], 'Cooked', 8), True))
        with self.assertRaises(ValueError):
            recipe_parts(composite('CookedCompositeAssembledNode', [raw], 'Raw'), True)

    def test_mixed_then_cooked_tree_matches_exact_native_snapshot(self):
        goal = {'kind': 'cooked', 'cookingStepId': 7, 'components': [
            {'kind': 'mixed', 'components': [{'kind': 'ingredient', 'id': 'i1'}, {'kind': 'ingredient', 'id': 'i2'}]}]}
        mixed = composite('MixedCompositeAssembledNode', [ingredient(2), ingredient(1)], 'Mixed')
        native = composite('CookedCompositeAssembledNode', [mixed], 'Cooked')
        self.assertEqual(recipe_parts(goal), recipe_parts(native, True))
        self.assertNotEqual(recipe_parts(goal), recipe_parts(composite('CookedCompositeAssembledNode', [ingredient(1), ingredient(2)], 'Cooked'), True))


if __name__ == '__main__':
    unittest.main()
