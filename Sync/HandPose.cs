using System;
using UnityEngine;

namespace FollowingSeasCoop.Sync
{
	internal enum FingerGrip
	{
		Handle,
		Cylinder,
		Support,
		Side,
		Trigger
	}

	internal struct PalmGrip
	{
		public Vector3 Position;
		public Quaternion Rotation;
		public FingerGrip Fingers;

		public PalmGrip(Vector3 position, Vector3 fingers, Vector3 palm, FingerGrip grip)
		{
			Position = position;
			Rotation = Quaternion.LookRotation(fingers, palm);
			Fingers = grip;
		}
	}

	internal sealed class HandPose : IDisposable
	{
		private readonly HumanPoseHandler _handler;
		private HumanPose _pose;
		private readonly Transform[] _hands = new Transform[2];
		private readonly Quaternion[] _basis = new Quaternion[2];
		private readonly Vector3[] _palmOffset = new Vector3[2];
		private readonly int[,,] _muscles = new int[2, 5, 4];
		private readonly bool[] _available = new bool[2];
		private readonly Transform[] _fingerBones = new Transform[30];
		private readonly Quaternion[] _animatedFingers = new Quaternion[30];
		private bool _hasFingerPose;
		private readonly Transform[] _skeleton;
		private readonly Vector3[] _positions;
		private readonly Quaternion[] _rotations;
		private readonly Vector3[] _scales;
		private readonly Quaternion[] _grippedFingers = new Quaternion[30];
		private readonly bool[] _writtenFingers = new bool[30];
		private readonly bool[,] _fingerAvailable = new bool[2, 5];

		public HandPose(Animator animator)
		{
			_handler = new HumanPoseHandler(animator.avatar, animator.transform);
			_pose.muscles = new float[HumanTrait.MuscleCount];
			_skeleton = animator.transform.GetComponentsInChildren<Transform>(true);
			_positions = new Vector3[_skeleton.Length];
			_rotations = new Quaternion[_skeleton.Length];
			_scales = new Vector3[_skeleton.Length];
			var missing = new System.Text.StringBuilder();
			string[] fingers = { "Thumb", "Index", "Middle", "Ring", "Little" };
			HumanBodyBones[,] bases =
			{
				{
					HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftIndexProximal,
					HumanBodyBones.LeftMiddleProximal,
					HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftLittleProximal
				},
				{
					HumanBodyBones.RightThumbProximal, HumanBodyBones.RightIndexProximal,
					HumanBodyBones.RightMiddleProximal,
					HumanBodyBones.RightRingProximal, HumanBodyBones.RightLittleProximal
				},
			};
			for (int side = 0; side < 2; side++)
			{
				Transform hand =
					animator.GetBoneTransform(side == 0 ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
				_hands[side] = hand;
				Transform index = animator.GetBoneTransform(bases[side, 1]);
				Transform middle = animator.GetBoneTransform(bases[side, 2]);
				Transform little = animator.GetBoneTransform(bases[side, 4]);
				if (hand != null && index != null && middle != null && little != null)
				{
					Vector3 forward = hand.InverseTransformPoint(middle.position);
					Vector3 across = hand.InverseTransformPoint(index.position) -
					                 hand.InverseTransformPoint(little.position);
					Vector3 palm = Vector3.Cross(forward, across) * (side == 0 ? -1f : 1f);
					if (forward.sqrMagnitude > 0.000001f && palm.sqrMagnitude > 0.000001f)
					{
						_basis[side] = Quaternion.LookRotation(forward, palm);
						_palmOffset[side] = forward * 0.65f;
						_available[side] = true;
					}
				}

				for (int finger = 0; finger < 5; finger++)
				{
					for (int joint = 0; joint < 3; joint++)
						_fingerBones[side * 15 + finger * 3 + joint] =
							animator.GetBoneTransform((HumanBodyBones)((int)bases[side, finger] + joint));
					for (int joint = 0; joint < 4; joint++)
					{
						string name = (side == 0 ? "Left " : "Right ") + fingers[finger] +
						              (joint == 3 ? " Spread" : " " + (joint + 1) + " Stretched");
						_muscles[side, finger, joint] = animator.GetBoneTransform(bases[side, finger]) != null
							? Array.IndexOf(HumanTrait.MuscleName, name)
							: -1;
					}

					bool complete = true;
					for (int joint = 0; joint < 3; joint++)
						complete &= _fingerBones[side * 15 + finger * 3 + joint] != null;
					for (int joint = 0; joint < 4; joint++) complete &= _muscles[side, finger, joint] >= 0;
					_fingerAvailable[side, finger] = complete;
					if (!complete) missing.Append(side == 0 ? " Left " : " Right ").Append(fingers[finger]);
				}
			}

			if (!_available[0] || !_available[1])
				Plugin.Log.LogWarning("[Hands] Avatar lacks mapped palm bones: " + animator.name);
			if (missing.Length > 0)
				Plugin.Log.LogWarning("[Hands] Incomplete finger mapping on " + animator.name + ":" + missing);
		}

		public bool Available(bool right) => _available[right ? 1 : 0];

		public Quaternion Rotation(bool right, PalmGrip grip) =>
			grip.Rotation * Quaternion.Inverse(_basis[right ? 1 : 0]);

		public Vector3 Wrist(bool right, PalmGrip grip) =>
			grip.Position - Rotation(right, grip) *
			Vector3.Scale(_palmOffset[right ? 1 : 0], _hands[right ? 1 : 0].lossyScale);

		public float Contact(bool right, PalmGrip grip)
		{
			int side = right ? 1 : 0;
			if (!_available[side]) return 0f;
			Vector3 palm = _hands[side].TransformPoint(_palmOffset[side]);
			return 1f - Mathf.InverseLerp(0.02f, 0.1f, Vector3.Distance(palm, grip.Position));
		}

		public void ApplyFingers(FingerGrip left, float leftWeight, FingerGrip right, float rightWeight)
		{
			if (leftWeight <= 0f && rightWeight <= 0f) return;
			Array.Clear(_writtenFingers, 0, _writtenFingers.Length);
			for (int i = 0; i < _fingerBones.Length; i++)
				if (_fingerBones[i] != null)
					_animatedFingers[i] = _fingerBones[i].localRotation;
			_hasFingerPose = true;
			for (int i = 0; i < _skeleton.Length; i++)
			{
				_positions[i] = _skeleton[i].localPosition;
				_rotations[i] = _skeleton[i].localRotation;
				_scales[i] = _skeleton[i].localScale;
			}

			try
			{
				_handler.GetHumanPose(ref _pose);
				for (int side = 0; side < 2; side++)
				{
					float weight = side == 0 ? leftWeight : rightWeight;
					FingerGrip grip = side == 0 ? left : right;
					if (weight <= 0f || !_available[side]) continue;
					for (int finger = 0; finger < 5; finger++)
					for (int joint = 0; joint < 4; joint++)
					{
						if (!_fingerAvailable[side, finger]) continue;
						int muscle = _muscles[side, finger, joint];
						if (muscle < 0) continue;
						float target;
						if (joint == 3) target = finger == 0 ? 0.3f : 0f;
						else if (finger == 0) target = joint == 0 ? 0.1f : -0.35f;
						else if (grip == FingerGrip.Trigger && finger == 1) target = joint == 0 ? 0.2f : -0.15f;
						else if (grip == FingerGrip.Support || grip == FingerGrip.Side)
							target = joint == 0 ? 0.25f : -0.15f;
						else target = grip == FingerGrip.Cylinder ? -0.45f : -0.7f;
						_pose.muscles[muscle] = Mathf.Lerp(_pose.muscles[muscle], target, weight);
					}
				}

				_handler.SetHumanPose(ref _pose);
				for (int i = 0; i < _fingerBones.Length; i++)
					if (_fingerBones[i] != null)
						_grippedFingers[i] = _fingerBones[i].localRotation;
			}
			finally
			{
				for (int i = 0; i < _skeleton.Length; i++)
				{
					_skeleton[i].localPosition = _positions[i];
					_skeleton[i].localRotation = _rotations[i];
					_skeleton[i].localScale = _scales[i];
				}
			}

			for (int i = 0; i < _fingerBones.Length; i++)
			{
				int side = i / 15;
				if (_fingerBones[i] != null && _available[side] && _fingerAvailable[side, i % 15 / 3] &&
				    (side == 0 ? leftWeight : rightWeight) > 0f)
				{
					_fingerBones[i].localRotation = _grippedFingers[i];
					_writtenFingers[i] = true;
				}
			}
		}

		public void ApplyPalm(bool right, PalmGrip grip, float weight)
		{
			int side = right ? 1 : 0;
			if (!_available[side] || weight <= 0f) return;
			Transform hand = _hands[side];
			hand.rotation = Quaternion.Slerp(hand.rotation, Rotation(right, grip), weight);
		}

		public void Dispose() => _handler.Dispose();

		public void RestoreFingers()
		{
			if (!_hasFingerPose) return;
			for (int i = 0; i < _fingerBones.Length; i++)
				if (_fingerBones[i] != null && _writtenFingers[i] &&
				    _fingerBones[i].localRotation.Equals(_grippedFingers[i]))
					_fingerBones[i].localRotation = _animatedFingers[i];
			_hasFingerPose = false;
		}
	}
}
