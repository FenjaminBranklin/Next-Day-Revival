"""Geometry and shipped-prerequisite tests for the permanent float kit."""
import hashlib
from pathlib import Path
import unittest

import numpy as np
from PIL import Image

import an2_amphibious_build as kit
from ndmesh import load

ROOT = Path(__file__).resolve().parents[1]


class AmphibiousAssetsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.shipped, cls.ranges = kit.land_gear_mask()
        cls.meshes = kit.kit_meshes()

    def test_mask_removes_only_named_original_land_gear(self):
        self.assertEqual({row[2] for row in self.ranges}, {
            'Part_Gear_Main_L', 'Part_Gear_Main_R', 'Part_Gear_Tail',
            'Pivot_Wheel_L', 'Pivot_Wheel_R', 'Pivot_Wheel_Tail'})
        self.assertEqual(sum(row[1] for row in self.ranges), 1104*3)
        indices = np.array(self.shipped[3])
        previous = 0
        for start, count, name in self.ranges:
            self.assertGreaterEqual(start, previous, name)
            self.assertEqual(start % 3, 0)
            self.assertEqual(count % 3, 0)
            self.assertLessEqual(start+count, len(indices))
            previous = start+count
        # Full equality to the tracked GLB was checked by land_gear_mask().
        # No world-scale conversion or plane reconstruction is emitted.
        rig = kit.rig_text(self.shipped, self.ranges)
        self.assertEqual(rig, (kit.ASSETS/'an2_amphib_rig.txt').read_text(encoding='ascii'))
        self.assertIn(hashlib.sha256((kit.ASSETS/'an2_body.ndmesh').read_bytes()).hexdigest(), rig)

    def test_generated_assets_are_reproducible_and_sound(self):
        for name, mesh in self.meshes.items():
            mesh.validate()
            self.assertLess(len(mesh.V), 65535)
            actual = load(str(kit.ASSETS/('an2_amphib_%s.ndmesh' % name)))
            for want, got in zip((mesh.V, mesh.N, mesh.T, mesh.IDX), actual):
                np.testing.assert_allclose(want, got, atol=5e-7)
            vertices, normals = np.array(mesh.V), np.array(mesh.N)
            triangles = np.array(mesh.IDX).reshape(-1, 3)
            geometric = np.cross(vertices[triangles[:, 1]]-vertices[triangles[:, 0]],
                                 vertices[triangles[:, 2]]-vertices[triangles[:, 0]])
            self.assertTrue(np.all(np.linalg.norm(geometric, axis=1) > 1e-8), name)
            self.assertTrue(np.all(np.sum(geometric*normals[triangles[:, 0]], axis=1) > 0), name)
            np.testing.assert_allclose(np.linalg.norm(normals, axis=1), 1., atol=1e-6)
            self.assertTrue(np.all(np.array(mesh.T) >= 0) and np.all(np.array(mesh.T) <= 1))

    def test_float_hulls_are_closed_outward_and_symmetric(self):
        for side in (-1., 1.):
            hull = kit.float_hull(side*kit.FLOAT_X)
            v = np.array(hull.V)
            tri = np.array(hull.IDX).reshape(-1, 3)
            volume = np.sum(np.einsum('ij,ij->i', v[tri[:, 0]],
                            np.cross(v[tri[:, 1]], v[tri[:, 2]])))/6
            self.assertGreater(volume, 3.)
            # Every geometric edge is shared by exactly two triangles.
            edges = {}
            for face in tri:
                for a, b in zip(face, np.roll(face, -1)):
                    edge = tuple(sorted((tuple(v[a]), tuple(v[b]))))
                    edges[edge] = edges.get(edge, 0)+1
            self.assertTrue(all(count == 2 for count in edges.values()))
        left, right = kit.float_hull(-2.), kit.float_hull(2.)
        np.testing.assert_allclose(np.array(left.V)+[4., 0., 0.], right.V)
        self.assertAlmostEqual(np.ptp(np.array(left.V)[:, 2]), 7.9)

    def test_wheels_retract_above_keel_and_have_matching_contacts(self):
        for name, kind, x, y, z, radius, cy, angle in kit.WHEELS:
            v = np.array(self.meshes[kind].V)
            self.assertAlmostEqual(v[:, 1].min()+y+kit.OFFSET[1], 0., places=6, msg=name)
            self.assertAlmostEqual(y+cy-radius+kit.OFFSET[1], 0., msg=name)
            # Sphere collider follows the same swing pivot as the visible tyre.
            retracted_y = y+cy*np.cos(np.radians(angle))+kit.OFFSET[1]
            self.assertGreater(retracted_y-radius, .8, name)
        self.assertEqual(kit.WHEELS[0][4], 0.)  # Existing main-axle origin retained.
        self.assertEqual(kit.WHEELS[2][4], -4.8)

    def test_float_bottom_boxes_stay_inside_hull(self):
        # Every box corner lies inside the interpolated polygon cross section.
        for _, y, z, sx, sy, sz in kit.FLOAT_BOXES:
            for zz in (z-sz*.5, z+sz*.5):
                a, b = next((a, b) for a, b in zip(kit.SECTIONS, kit.SECTIONS[1:]) if a[0] <= zz <= b[0])
                t = (zz-a[0])/(b[0]-a[0])
                _, w, top, bottom = (1-t)*np.array(a)+t*np.array(b)
                self.assertLess(y+sy*.5, top)
                # At x=half collider width the V bottom rises linearly.
                for xx in (-sx*.5, sx*.5):
                    lower = bottom+abs(xx)/(.9*w)*(top-bottom)*.18
                    self.assertGreaterEqual(y-sy*.5+1e-6, lower)
                    self.assertLess(abs(xx), .9*w)

    def test_scale_is_proven_against_player_capsule(self):
        source = (ROOT/'Revival.PlayerAn2.cs').read_text(encoding='utf-8')
        self.assertIn('internal const float K = 2.8f;', source)
        self.assertAlmostEqual(kit.CAPSULE_U/kit.K, 1.785714, places=5)
        self.assertAlmostEqual(18.18*kit.K/kit.CAPSULE_U, 10.1808)
        self.assertAlmostEqual(7.9*kit.K/kit.CAPSULE_U, 4.424)
        self.assertAlmostEqual(.8*kit.K, 2.24)
        self.assertAlmostEqual(.5*kit.K, 1.4)
        self.assertIn('An2Amphibious.Attach(root.transform);', source)
        self.assertIn('root.transform.localPosition = An2Amphibious.Offset * Scale;', source)
        self.assertIn('_body = An2Amphibious.Load(_body);', source)
        self.assertIn('Parked = An2Amphibious.Parked;', source)
        runtime = (ROOT/'Revival.An2Amphibious.cs').read_text(encoding='ascii')
        for callback in ('void Update(', 'void LateUpdate(', 'void FixedUpdate('):
            self.assertNotIn(callback, runtime)

    def test_texture_and_client_packaging(self):
        np.testing.assert_array_equal(np.array(kit.texture()), np.array(Image.open(kit.ASSETS/'an2_amphib_diffuse.png')))
        nrm = np.array(Image.open(kit.ASSETS/'an2_amphib_normal.png'))
        self.assertTrue(np.all(nrm == [128, 128, 255]))
        runtime_assets = ['an2_amphib_%s.ndmesh' % p for p in ('floats', 'main', 'rear')]
        runtime_assets += ['an2_amphib_diffuse.png', 'an2_amphib_normal.png', 'an2_amphib_rig.txt']
        for path in ('build.ps1', 'verify.py'):
            source = (ROOT/path).read_text(encoding='utf-8')
            for asset in runtime_assets:
                self.assertIn('"'+asset+'"', source, path)
        self.assertIn('"Revival.An2Amphibious.cs"', (ROOT/'sync_public.py').read_text(encoding='utf-8'))


if __name__ == '__main__':
    unittest.main()
