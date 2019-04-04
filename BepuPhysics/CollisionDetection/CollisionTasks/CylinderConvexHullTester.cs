﻿using BepuPhysics.Collidables;
using BepuUtilities;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace BepuPhysics.CollisionDetection.CollisionTasks
{
    public struct CylinderConvexHullTester : IPairTester<CylinderWide, ConvexHullWide, Convex4ContactManifoldWide>
    {
        public int BatchSize => 32;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void ProjectOntoCap(in Vector3 capCenter, in Matrix3x3 cylinderOrientation, float inverseNDotAY, in Vector3 localNormal, in Vector3 point, out Vector2 projected)
        {
            var pointToCapCenter = capCenter - point;
            var t = Vector3.Dot(pointToCapCenter, cylinderOrientation.Y) * inverseNDotAY;
            var projectionOffsetB = localNormal * t;
            var projectedPoint = point + projectionOffsetB;
            var capCenterToProjectedPoint = projectedPoint - capCenter;
            projected.X = Vector3.Dot(capCenterToProjectedPoint, cylinderOrientation.X);
            projected.Y = Vector3.Dot(capCenterToProjectedPoint, cylinderOrientation.Z);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IntersectLineCircle(in Vector2 linePosition, in Vector2 lineDirection, float radius, out float tMin, out float tMax)
        {
            //||linePosition + lineDirection * t|| = radius
            //dot(linePosition + lineDirection * t, linePosition + lineDirection * t) = radius * radius
            //dot(linePosition, linePosition) - radius * radius + t * 2 * dot(linePosition, lineDirection) + t^2 * dot(lineDirection, lineDirection) = 0
            var a = Vector2.Dot(lineDirection, lineDirection);
            var inverseA = 1f / a;
            var b = Vector2.Dot(linePosition, lineDirection);
            var c = Vector2.Dot(linePosition, linePosition);
            var radiusSquared = radius * radius;
            c -= radiusSquared;
            var d = b * b - a * c;
            if (d < 0)
            {
                tMin = 0;
                tMax = 0;
                return false;
            }
            var tOffset = (float)Math.Sqrt(d) * inverseA;
            var tBase = -b * inverseA;
            if (a < 1e-12f && a > 1e-12f)
            {
                //If the projected line direction is zero, just compress the interval to tBase.
                tMin = tBase;
                tMax = tBase;
            }
            else
            {
                tMin = tBase - tOffset;
                tMax = tBase + tOffset;
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Test(ref CylinderWide a, ref ConvexHullWide b, ref Vector<float> speculativeMargin, ref Vector3Wide offsetB, ref QuaternionWide orientationA, ref QuaternionWide orientationB, int pairCount, out Convex4ContactManifoldWide manifold)
        {
            Matrix3x3Wide.CreateFromQuaternion(orientationA, out var cylinderOrientation);
            Matrix3x3Wide.CreateFromQuaternion(orientationB, out var hullOrientation);
            Matrix3x3Wide.MultiplyByTransposeWithoutOverlap(cylinderOrientation, hullOrientation, out var hullLocalCylinderOrientation);

            Matrix3x3Wide.TransformByTransposedWithoutOverlap(offsetB, hullOrientation, out var localOffsetB);
            Vector3Wide.Negate(localOffsetB, out var localOffsetA);
            Vector3Wide.Length(localOffsetA, out var centerDistance);
            Vector3Wide.Scale(localOffsetA, Vector<float>.One / centerDistance, out var initialNormal);
            var useInitialFallback = Vector.LessThan(centerDistance, new Vector<float>(1e-8f));
            initialNormal.X = Vector.ConditionalSelect(useInitialFallback, Vector<float>.Zero, initialNormal.X);
            initialNormal.Y = Vector.ConditionalSelect(useInitialFallback, Vector<float>.One, initialNormal.Y);
            initialNormal.Z = Vector.ConditionalSelect(useInitialFallback, Vector<float>.Zero, initialNormal.Z);
            var hullSupportFinder = default(ConvexHullSupportFinder);
            var cylinderSupportFinder = default(CylinderSupportFinder);
            ManifoldCandidateHelper.CreateInactiveMask(pairCount, out var inactiveLanes);
            b.EstimateEpsilonScale(inactiveLanes, out var hullEpsilonScale);
            var epsilonScale = Vector.Min(Vector.Max(a.HalfLength, a.Radius), hullEpsilonScale);
            var depthThreshold = -speculativeMargin;
            DepthRefiner<ConvexHull, ConvexHullWide, ConvexHullSupportFinder, Cylinder, CylinderWide, CylinderSupportFinder>.FindMinimumDepth(
                b, a, localOffsetA, hullLocalCylinderOrientation, ref hullSupportFinder, ref cylinderSupportFinder, initialNormal, inactiveLanes, 1e-5f * epsilonScale, depthThreshold,
                out var depth, out var localNormal, out var closestOnHull);

            inactiveLanes = Vector.BitwiseOr(inactiveLanes, Vector.LessThan(depth, depthThreshold));
            if (Vector.LessThanAll(inactiveLanes, Vector<int>.Zero))
            {
                //No contacts generated.
                manifold = default;
                return;
            }

            //Identify the cylinder feature.
            Vector3Wide.Scale(localNormal, depth, out var closestOnCylinderOffset);
            Vector3Wide.Add(closestOnHull, closestOnCylinderOffset, out var closestOnCylinder);
            Matrix3x3Wide.TransformByTransposedWithoutOverlap(localNormal, hullLocalCylinderOrientation, out var localNormalInA);
            var inverseNormalDotAY = Vector<float>.One / localNormalInA.Y;
            var useCap = Vector.GreaterThan(Vector.Abs(localNormalInA.Y), new Vector<float>(0.70710678118f));
            Vector3Wide capNormal, capCenter;
            Vector2Wide interior0, interior1, interior2, interior3;
            if (Vector.LessThanAny(useCap, Vector<int>.Zero))
            {
                Vector3Wide.ConditionallyNegate(Vector.LessThan(localNormalInA.Y, Vector<float>.Zero), hullLocalCylinderOrientation.Y, out capNormal);
                Vector3Wide.Scale(capNormal, a.HalfLength, out capCenter);
                Vector3Wide.Add(capCenter, localOffsetA, out capCenter);

                BoxCylinderTester.GenerateInteriorPoints(a, localNormal, closestOnCylinder, out interior0, out interior1, out interior2, out interior3);
            }

            Vector3Wide cylinderSideEdgeCenter;
            if (Vector.GreaterThanAny(useCap, Vector<int>.Zero))
            {
                //If the contact is on the cylinder's side, use the closestOnHull-derived position rather than resampling the support function with the local normal to avoid numerical noise.
                Vector3Wide.Subtract(closestOnCylinder, localOffsetA, out var cylinderToClosestOnCylinder);
                Vector3Wide.Dot(cylinderToClosestOnCylinder, hullLocalCylinderOrientation.Y, out var cylinderLocalClosestOnCylinderY);
                Vector3Wide.Scale(hullLocalCylinderOrientation.Y, cylinderLocalClosestOnCylinderY, out var cylinderEdgeCenterToClosestOnCylinder);
                Vector3Wide.Subtract(closestOnCylinder, cylinderEdgeCenterToClosestOnCylinder, out cylinderSideEdgeCenter);
            }

            Helpers.FillVectorWithLaneIndices(out var slotOffsetIndices);
            var boundingPlaneEpsilon = 1e-4f * epsilonScale;
            for (int slotIndex = 0; slotIndex < pairCount; ++slotIndex)
            {
                if (inactiveLanes[slotIndex] < 0)
                    continue;
                ref var hull = ref b.Hulls[slotIndex];
                ConvexHullTestHelper.PickRepresentativeFace(ref hull, slotIndex, ref localNormal, closestOnHull, slotOffsetIndices, ref boundingPlaneEpsilon, out var slotHullFaceNormal, out var slotLocalNormal, out var bestFaceIndex);
                hull.GetVertexIndicesForFace(bestFaceIndex, out var faceVertexIndices);

                if (useCap[slotIndex] < 0)
                {
                    //We can create up to 2 contacts per hull edge.
                    var maximumCandidateCount = faceVertexIndices.Length * 2;
                    var candidates = stackalloc ManifoldCandidateScalar[maximumCandidateCount];
                    var candidateCount = 0;
                    //The cap is the representative feature. Clip the hull's edges against the cap's circle, and test the cylinder's heuristically chosen 'vertices' against the hull edges for containment.
                    //Note that we work on the surface of the cap and post-project back onto the hull.
                    Vector3Wide.ReadSlot(ref capCenter, slotIndex, out var slotCapCenter);
                    Vector3Wide.ReadSlot(ref capNormal, slotIndex, out var slotCapNormal);
                    Matrix3x3Wide.ReadSlot(ref cylinderOrientation, slotIndex, out var slotCylinderOrientation);
                    var slotInverseNDotAY = inverseNormalDotAY[slotIndex];

                    ref var interior0Slot = ref GatherScatter.GetOffsetInstance(ref interior0, slotIndex);
                    ref var interior1Slot = ref GatherScatter.GetOffsetInstance(ref interior1, slotIndex);
                    ref var interior2Slot = ref GatherScatter.GetOffsetInstance(ref interior2, slotIndex);
                    ref var interior3Slot = ref GatherScatter.GetOffsetInstance(ref interior3, slotIndex);
                    var interiorPointsX = new Vector4(interior0Slot.X[0], interior1Slot.X[0], interior2Slot.X[0], interior3Slot.X[0]);
                    var interiorPointsY = new Vector4(interior0Slot.Y[0], interior1Slot.Y[0], interior2Slot.Y[0], interior3Slot.Y[0]);
                    var slotRadius = a.Radius[slotIndex];

                    var previousIndex = faceVertexIndices[faceVertexIndices.Length - 1];
                    Vector3Wide.ReadSlot(ref hull.Points[previousIndex.BundleIndex], previousIndex.InnerIndex, out var hullFaceOrigin);
                    ProjectOntoCap(slotCapCenter, slotCylinderOrientation, slotInverseNDotAY, slotLocalNormal, hullFaceOrigin, out var previousVertex);
                    var maximumInteriorContainmentDots = Vector4.Zero;

                    for (int i = 0; i < faceVertexIndices.Length; ++i)
                    {
                        var index = faceVertexIndices[i];
                        Vector3Wide.ReadSlot(ref hull.Points[previousIndex.BundleIndex], previousIndex.InnerIndex, out var hullVertex);
                        ProjectOntoCap(slotCapCenter, slotCylinderOrientation, slotInverseNDotAY, slotLocalNormal, hullVertex, out var vertex);

                        //Test all the cap's interior points against this edge's plane normal (which, since we've projected the vertex, is just a perp dot product).
                        var hullEdgeOffset = vertex - previousVertex;
                        var previousStartX = new Vector4(previousVertex.X);
                        var previousStartY = new Vector4(previousVertex.Y);
                        var hullEdgeOffsetX = new Vector4(hullEdgeOffset.X);
                        var hullEdgeOffsetY = new Vector4(hullEdgeOffset.Y);
                        var interiorPointContainmentDots = (interiorPointsX - previousStartX) * hullEdgeOffsetY - (interiorPointsY - previousStartY) * hullEdgeOffsetX;
                        maximumInteriorContainmentDots = Vector4.Max(interiorPointContainmentDots, maximumInteriorContainmentDots);

                        //Test the projected hull edge against the cap.
                        if (IntersectLineCircle(previousVertex, hullEdgeOffset, slotRadius, out var tMin, out var tMax))
                        {
                            //We now have a convex hull edge interval. Add contacts for it.
                            //Create max contact if max >= min.
                            //Create min if min < max and min > 0.
                            var startId = (previousIndex.BundleIndex << BundleIndexing.VectorShift) + previousIndex.InnerIndex;
                            var endId = (index.BundleIndex << BundleIndexing.VectorShift) + index.InnerIndex;
                            var baseFeatureId = (startId ^ endId) << 8;
                            if (tMax >= tMin && candidateCount < maximumCandidateCount)
                            {
                                //Create max contact.
                                var newContactIndex = candidateCount++;
                                ref var candidate = ref candidates[newContactIndex];
                                Unsafe.As<float, Vector2>(ref candidate.X) = hullEdgeOffset * tMax + previousVertex;
                                candidate.FeatureId = baseFeatureId + endId;

                            }
                            if (tMin < tMax && tMin > 0 && candidateCount < maximumCandidateCount)
                            {
                                //Create min contact.
                                var newContactIndex = candidateCount++;
                                ref var candidate = ref candidates[newContactIndex];
                                Unsafe.As<float, Vector2>(ref candidate.X) = hullEdgeOffset * tMin + previousVertex;
                                candidate.FeatureId = baseFeatureId + startId;

                            }
                        }

                        previousIndex = index;
                        previousVertex = vertex;
                    }

                    if (candidateCount < maximumCandidateCount)
                    {
                        //Try adding the cylinder 'vertex' contacts.
                        //We took the maximum of all interior-hulledgeplane tests; if a vertex is outside any edge plane, the maximum dot will be positive.
                        if (maximumInteriorContainmentDots.X <= 0)
                        {
                            ref var candidate = ref candidates[candidateCount++];
                            candidate.X = interiorPointsX.X;
                            candidate.Y = interiorPointsY.X;
                            candidate.FeatureId = 0;
                        }
                        if (candidateCount == maximumCandidateCount)
                            goto SkipVertexCandidates;
                        if (maximumInteriorContainmentDots.Y <= 0)
                        {
                            ref var candidate = ref candidates[candidateCount++];
                            candidate.X = interiorPointsX.Y;
                            candidate.Y = interiorPointsY.Y;
                            candidate.FeatureId = 1;
                        }
                        if (candidateCount == maximumCandidateCount)
                            goto SkipVertexCandidates;
                        if (maximumInteriorContainmentDots.Z <= 0)
                        {
                            ref var candidate = ref candidates[candidateCount++];
                            candidate.X = interiorPointsX.Z;
                            candidate.Y = interiorPointsY.Z;
                            candidate.FeatureId = 2;
                        }
                        if (candidateCount < maximumCandidateCount && maximumInteriorContainmentDots.W <= 0)
                        {
                            ref var candidate = ref candidates[candidateCount++];
                            candidate.X = interiorPointsX.W;
                            candidate.Y = interiorPointsY.W;
                            candidate.FeatureId = 3;
                        }
                    SkipVertexCandidates:;
                        //We have found all contacts for this hull slot. There may be more contacts than we want (4), so perform a reduction.
                        Vector3Wide.ReadSlot(ref offsetB, slotIndex, out var slotOffsetB);
                        Vector3Wide.ReadSlot(ref hullLocalCylinderOrientation.X, slotIndex, out var slotCylinderFaceX);
                        Vector3Wide.ReadSlot(ref hullLocalCylinderOrientation.Z, slotIndex, out var slotCylinderFaceY);
                        //Note that we're working on the cylinder's cap, so the parameters get flipped around. Gets pushed back onto the hull in the postpass.
                        ManifoldCandidateHelper.Reduce(candidates, candidateCount, slotHullFaceNormal, -slotLocalNormal, hullFaceOrigin, slotCapCenter, slotCylinderFaceX, slotCylinderFaceY, epsilonScale[slotIndex], depthThreshold[slotIndex],
                           slotCylinderOrientation, -slotOffsetB, slotIndex, ref manifold);
                    }
                }
                else
                {
                    //The side edge is the representative feature. Clip the cylinder's side edge against the hull edges; similar to capsule-hull. 
                }
            }
            //Push the manifold onto the hull. This is useful if we ever end up building a 'HullReduction' like we have for MeshReduction, consistent with the other hull-(nottriangle) pairs.
            //The reduction does not assign the normal. Fill it in.
            Matrix3x3Wide.TransformWithoutOverlap(localNormal, hullOrientation, out manifold.Normal);
            Vector3Wide.Scale(manifold.Normal, manifold.Depth0, out var offset0);
            Vector3Wide.Scale(manifold.Normal, manifold.Depth1, out var offset1);
            Vector3Wide.Scale(manifold.Normal, manifold.Depth2, out var offset2);
            Vector3Wide.Scale(manifold.Normal, manifold.Depth3, out var offset3);
            Vector3Wide.Subtract(manifold.OffsetA0, offset0, out manifold.OffsetA0);
            Vector3Wide.Subtract(manifold.OffsetA1, offset1, out manifold.OffsetA1);
            Vector3Wide.Subtract(manifold.OffsetA2, offset2, out manifold.OffsetA2);
            Vector3Wide.Subtract(manifold.OffsetA3, offset3, out manifold.OffsetA3);
        }

        public void Test(ref CylinderWide a, ref ConvexHullWide b, ref Vector<float> speculativeMargin, ref Vector3Wide offsetB, ref QuaternionWide orientationB, int pairCount, out Convex4ContactManifoldWide manifold)
        {
            throw new NotImplementedException();
        }

        public void Test(ref CylinderWide a, ref ConvexHullWide b, ref Vector<float> speculativeMargin, ref Vector3Wide offsetB, int pairCount, out Convex4ContactManifoldWide manifold)
        {
            throw new NotImplementedException();
        }
    }
}