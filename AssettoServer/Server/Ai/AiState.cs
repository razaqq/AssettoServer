﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Drawing;
using System.Numerics;
using AssettoServer.Network.Packets.Outgoing;
using AssettoServer.Server.Configuration;
using AssettoServer.Server.Weather;
using AssettoServer.Utils;
using JPBotelho;
using Serilog;

namespace AssettoServer.Server.Ai
{
    public class AiState
    {
        public CarStatus Status { get; } = new();
        public EntryCar EntryCar { get; }
        public bool Initialized { get; set; }
        public TrafficSplinePoint CurrentSplinePoint { get; private set; } = null!;
        public TrafficMapView MapView { get; private set; }
        public long SpawnProtectionEnds { get; set; }
        public float SafetyDistanceSquared { get; set; } = 20 * 20;
        public float Acceleration { get; set; }
        public float CurrentSpeed { get; private set; }
        public float TargetSpeed { get; private set; }
        public float InitialMaxSpeed { get; private set; }
        public float MaxSpeed { get; private set; }
        public Color Color { get; private set; }
        public byte SpawnCounter { get; private set; }

        private const float WalkingSpeed = 7 / 3.6f;

        private Vector3 _startTangent;
        private Vector3 _endTangent;

        private float _currentVecLength;
        private float _currentVecProgress;
        private long _lastTick;
        private bool _stoppedForObstacle;
        private long _stoppedForObstacleSince;
        private long _ignoreObstaclesUntil;
        private long _stoppedForCollisionUntil;
        private long _obstacleHonkStart;
        private long _obstacleHonkEnd;
        private CarStatusFlags _indicator = 0;
        private TrafficSplineJunction? _nextJunction;
        private bool _junctionPassed;
        private float _endIndicatorDistance;

        private readonly ACServerConfiguration _configuration;
        private readonly AiBehavior _aiBehavior;
        private readonly TrafficMap _trafficMap;
        private readonly SessionManager _sessionManager;
        private readonly EntryCarManager _entryCarManager;
        private readonly WeatherManager _weatherManager;

        private static readonly ImmutableList<Color> CarColors = new List<Color>()
        {
            Color.FromArgb(13, 17, 22),
            Color.FromArgb(19, 24, 31),
            Color.FromArgb(28, 29, 33),
            Color.FromArgb(12, 13, 24),
            Color.FromArgb(11, 20, 33),
            Color.FromArgb(151, 154, 151),
            Color.FromArgb(153, 157, 160),
            Color.FromArgb(194, 196, 198 ),
            Color.FromArgb(234, 234, 234),
            Color.FromArgb(255, 255, 255),
            Color.FromArgb(182, 17, 27),
            Color.FromArgb(218, 25, 24),
            Color.FromArgb(73, 17, 29),
            Color.FromArgb(35, 49, 85),
            Color.FromArgb(28, 53, 81),
            Color.FromArgb(37, 58, 167),
            Color.FromArgb(21, 92, 45),
            Color.FromArgb(18, 46, 43),
        }.ToImmutableList();

        public AiState(EntryCar entryCar, AiBehavior aiBehavior, SessionManager sessionManager, WeatherManager weatherManager, ACServerConfiguration configuration, TrafficMap trafficMap, EntryCarManager entryCarManager)
        {
            EntryCar = entryCar;
            _aiBehavior = aiBehavior;
            _sessionManager = sessionManager;
            _weatherManager = weatherManager;
            _configuration = configuration;
            _trafficMap = trafficMap;
            _entryCarManager = entryCarManager;
            MapView = new TrafficMapView();

            _lastTick = _sessionManager.ServerTimeMilliseconds;
        }

        private void SetRandomSpeed()
        {
            float variation = _configuration.Extra.AiParams.MaxSpeedMs * _configuration.Extra.AiParams.MaxSpeedVariationPercent;

            float fastLaneOffset = 0;
            if (CurrentSplinePoint.Left != null)
            {
                fastLaneOffset = _configuration.Extra.AiParams.RightLaneOffsetMs;
            }
            InitialMaxSpeed = _configuration.Extra.AiParams.MaxSpeedMs + fastLaneOffset - (variation / 2) + (float)Random.Shared.NextDouble() * variation;
            CurrentSpeed = InitialMaxSpeed;
            TargetSpeed = InitialMaxSpeed;
            MaxSpeed = InitialMaxSpeed;
        }

        private void SetRandomColor()
        {
            Color = CarColors[Random.Shared.Next(CarColors.Count)];
        }

        public void Teleport(TrafficSplinePoint point)
        {
            MapView.Clear();
            CurrentSplinePoint = point;
            if (!MapView.TryNext(CurrentSplinePoint, out var nextPoint))
                throw new InvalidOperationException($"Cannot get next spline point for {CurrentSplinePoint.Id}");
            _currentVecLength = (nextPoint.Position - CurrentSplinePoint.Position).Length();
            _currentVecProgress = 0;
            
            CalculateTangents();
            
            SetRandomSpeed();
            SetRandomColor();
            
            SpawnProtectionEnds = _sessionManager.ServerTimeMilliseconds + Random.Shared.Next(_configuration.Extra.AiParams.MinSpawnProtectionTimeMilliseconds, _configuration.Extra.AiParams.MaxSpawnProtectionTimeMilliseconds);
            SafetyDistanceSquared = Random.Shared.Next((int)Math.Round(_configuration.Extra.AiParams.MinAiSafetyDistanceSquared * (1.0f / _configuration.Extra.AiParams.TrafficDensity)),
                (int)Math.Round(_configuration.Extra.AiParams.MaxAiSafetyDistanceSquared * (1.0f / _configuration.Extra.AiParams.TrafficDensity)));
            _stoppedForCollisionUntil = 0;
            _ignoreObstaclesUntil = 0;
            _obstacleHonkEnd = 0;
            _obstacleHonkStart = 0;
            _indicator = 0;
            _nextJunction = null;
            _junctionPassed = false;
            _endIndicatorDistance = 0;
            _lastTick = _sessionManager.ServerTimeMilliseconds;
            SpawnCounter++;
            Initialized = true;
            Update();
        }

        private void CalculateTangents()
        {
            if (!MapView.TryNext(CurrentSplinePoint, out var nextPoint))
                throw new InvalidOperationException("Cannot get next spline point");
            
            if (MapView.TryPrevious(CurrentSplinePoint, out var previousPoint))
            {
                _startTangent = (nextPoint.Position - previousPoint.Position) * 0.5f;
            }
            else
            {
                _startTangent = (nextPoint.Position - CurrentSplinePoint.Position) * 0.5f;
            }

            if (MapView.TryNext(CurrentSplinePoint, out var nextNextPoint, 2))
            {
                _endTangent = (nextNextPoint.Position - CurrentSplinePoint.Position) * 0.5f;
            }
            else
            {
                _endTangent = (nextPoint.Position - CurrentSplinePoint.Position) * 0.5f;
            }
        }

        public bool Move(float progress)
        {
            bool recalculateTangents = false;
            while (progress > _currentVecLength)
            {
                progress -= _currentVecLength;
                
                if (!MapView.TryNext(CurrentSplinePoint, out var nextPoint)
                    || !MapView.TryNext(nextPoint, out var nextNextPoint))
                {
                    return false;
                }

                CurrentSplinePoint = nextPoint;
                _currentVecLength = (nextNextPoint.Position - CurrentSplinePoint.Position).Length();
                recalculateTangents = true;

                if (_junctionPassed)
                {
                    _endIndicatorDistance -= _currentVecLength;

                    if (_endIndicatorDistance < 0)
                    {
                        _indicator = 0;
                        _junctionPassed = false;
                        _endIndicatorDistance = 0;
                    }
                }
                
                if (_nextJunction != null && CurrentSplinePoint.JunctionEnd == _nextJunction)
                {
                    _junctionPassed = true;
                    _endIndicatorDistance = _nextJunction.IndicateDistancePost;
                    _nextJunction = null;
                }
            }

            if (recalculateTangents)
            {
                CalculateTangents();
            }

            _currentVecProgress = progress;

            return true;
        }

        public bool CanSpawn(Vector3 spawnPoint)
        {
            return EntryCar.CanSpawnAiState(spawnPoint, this);
        }

        private (AiState? ClosestAiState, float ClosestAiStateDistance, float MaxSpeed) SplineLookahead()
        {
            float maxCornerBrakingDistance = PhysicsUtils.CalculateBrakingDistance(CurrentSpeed - PhysicsUtils.CalculateMaxCorneringSpeed(_trafficMap.MinRadius, EntryCar.AiCorneringSpeedFactor), 
                                                 EntryCar.AiDeceleration * EntryCar.AiCorneringBrakeForceFactor) 
                                             * EntryCar.AiCorneringBrakeDistanceFactor;
            float maxBrakingDistance = Math.Max(maxCornerBrakingDistance, 50);
            
            AiState? closestAiState = null;
            float closestAiStateDistance = float.MaxValue;
            bool junctionFound = false;
            float distanceTravelled = 0;
            var point = CurrentSplinePoint ?? throw new InvalidOperationException("CurrentSplinePoint is null");
            float maxSpeed = float.MaxValue;
            while (distanceTravelled < maxBrakingDistance)
            {
                distanceTravelled += point.Length;
                point = MapView.Next(point);
                if (point == null)
                    break;

                if (!junctionFound && point.JunctionStart != null && distanceTravelled < point.JunctionStart.IndicateDistancePre)
                {
                    var indicator = MapView.WillTakeJunction(point.JunctionStart) ? point.JunctionStart.IndicateWhenTaken : point.JunctionStart.IndicateWhenNotTaken;
                    if (indicator != 0)
                    {
                        _indicator = indicator;
                        _nextJunction = point.JunctionStart;
                        junctionFound = true;
                    }
                }

                if (closestAiState == null && _aiBehavior.AiStatesBySplinePoint.TryGetValue(point, out var candidate))
                {
                    closestAiState = candidate;
                    closestAiStateDistance = Vector3.Distance(Status.Position, closestAiState.Status.Position);
                }

                float maxCorneringSpeed = PhysicsUtils.CalculateMaxCorneringSpeed(point.Radius, EntryCar.AiCorneringSpeedFactor);
                if (maxCorneringSpeed < CurrentSpeed)
                {
                    float brakingDistance = PhysicsUtils.CalculateBrakingDistance(CurrentSpeed - maxCorneringSpeed,
                                                EntryCar.AiDeceleration * EntryCar.AiCorneringBrakeForceFactor)
                                            * EntryCar.AiCorneringBrakeDistanceFactor;

                    if (brakingDistance > distanceTravelled)
                    {
                        maxSpeed = Math.Min(maxCorneringSpeed, maxSpeed);
                    }
                }
            }

            return (closestAiState, closestAiStateDistance, maxSpeed);
        }

        private (EntryCar? entryCar, float distance) FindClosestPlayerObstacle()
        {
            EntryCar? closestCar = null;
            float minDistance = float.MaxValue;
            for (var i = 0; i < _entryCarManager.EntryCars.Length; i++)
            {
                var playerCar = _entryCarManager.EntryCars[i];
                if (playerCar.Client?.HasSentFirstUpdate == true)
                {
                    float distance = Vector3.DistanceSquared(playerCar.Status.Position, Status.Position);

                    if (distance < minDistance && GetAngleToCar(playerCar.Status) is > 166 and < 194)
                    {
                        minDistance = distance;
                        closestCar = playerCar;
                    }
                }
            }

            if (closestCar != null)
            {
                return (closestCar, MathF.Sqrt(minDistance));
            }

            return (null, float.MaxValue);
        }

        private bool IsObstacle(EntryCar playerCar)
        {
            float aiRectWidth = 4; // Lane width
            float halfAiRectWidth = aiRectWidth / 2;
            float aiRectLength = 10; // length of rectangle infront of ai traffic
            float aiRectOffset = 1; // offset of the rectangle from ai position

            float obstacleRectWidth = 1; // width of obstacle car 
            float obstacleRectLength = 1; // length of obstacle car
            float halfObstacleRectWidth = obstacleRectWidth / 2;
            float halfObstanceRectLength = obstacleRectLength / 2;

            Vector3 forward = Vector3.Transform(-Vector3.UnitX, Matrix4x4.CreateRotationY(Status.Rotation.X));
            Matrix4x4 aiViewMatrix = Matrix4x4.CreateLookAt(Status.Position, Status.Position + forward, Vector3.UnitY);

            Matrix4x4 targetWorldViewMatrix = Matrix4x4.CreateRotationY(playerCar.Status.Rotation.X) * Matrix4x4.CreateTranslation(playerCar.Status.Position) * aiViewMatrix;

            Vector3 targetFrontLeft = Vector3.Transform(new Vector3(-halfObstanceRectLength, 0, halfObstacleRectWidth), targetWorldViewMatrix);
            Vector3 targetFrontRight = Vector3.Transform(new Vector3(-halfObstanceRectLength, 0, -halfObstacleRectWidth), targetWorldViewMatrix);
            Vector3 targetRearLeft = Vector3.Transform(new Vector3(halfObstanceRectLength, 0, halfObstacleRectWidth), targetWorldViewMatrix);
            Vector3 targetRearRight = Vector3.Transform(new Vector3(halfObstanceRectLength, 0, -halfObstacleRectWidth), targetWorldViewMatrix);

            static bool isPointInside(Vector3 point, float width, float length, float offset)
                => MathF.Abs(point.X) >= width || (-point.Z >= offset && -point.Z <= offset + length);

            bool isObstacle = isPointInside(targetFrontLeft, halfAiRectWidth, aiRectLength, aiRectOffset)
                              || isPointInside(targetFrontRight, halfAiRectWidth, aiRectLength, aiRectOffset)
                              || isPointInside(targetRearLeft, halfAiRectWidth, aiRectLength, aiRectOffset)
                              || isPointInside(targetRearRight, halfAiRectWidth, aiRectLength, aiRectOffset);

            return isObstacle;
        }

        public void DetectObstacles()
        {
            if (!Initialized) return;
            
            if (_sessionManager.ServerTimeMilliseconds < _ignoreObstaclesUntil)
            {
                SetTargetSpeed(MaxSpeed);
                return;
            }

            if (_sessionManager.ServerTimeMilliseconds < _stoppedForCollisionUntil)
            {
                SetTargetSpeed(0);
                return;
            }
            
            float targetSpeed = InitialMaxSpeed;
            bool hasObstacle = false;

            var splineLookahead = SplineLookahead();
            var playerObstacle = FindClosestPlayerObstacle();

            if (playerObstacle.distance < 10 || splineLookahead.ClosestAiStateDistance < 10)
            {
                targetSpeed = 0;
                hasObstacle = true;
            }
            else if (playerObstacle.distance < splineLookahead.ClosestAiStateDistance && playerObstacle.entryCar != null)
            {
                float playerSpeed = playerObstacle.entryCar.Status.Velocity.Length();

                if (playerSpeed < 0.1f)
                {
                    playerSpeed = 0;
                }

                if ((playerSpeed < CurrentSpeed || playerSpeed == 0)
                    && playerObstacle.distance < PhysicsUtils.CalculateBrakingDistance(CurrentSpeed - playerSpeed, EntryCar.AiDeceleration) * 2 + 20)
                {
                    targetSpeed = Math.Max(WalkingSpeed, playerSpeed);
                    hasObstacle = true;
                }
            }
            else if (splineLookahead.ClosestAiState != null)
            {
                // AI in front has obstacle
                if (splineLookahead.ClosestAiState.TargetSpeed < splineLookahead.ClosestAiState.MaxSpeed)
                {
                    if ((splineLookahead.ClosestAiState.CurrentSpeed < CurrentSpeed || splineLookahead.ClosestAiState.CurrentSpeed == 0)
                        && splineLookahead.ClosestAiStateDistance < PhysicsUtils.CalculateBrakingDistance(CurrentSpeed - splineLookahead.ClosestAiState.CurrentSpeed, EntryCar.AiDeceleration) * 2 + 20)
                    {
                        targetSpeed = Math.Max(WalkingSpeed, splineLookahead.ClosestAiState.CurrentSpeed);
                        hasObstacle = true;
                    }
                }
                // AI in front is in clean air, so we just adapt our max speed
                else if(Math.Pow(splineLookahead.ClosestAiStateDistance, 2) < SafetyDistanceSquared)
                {
                    MaxSpeed = Math.Max(WalkingSpeed, splineLookahead.ClosestAiState.CurrentSpeed);
                    targetSpeed = MaxSpeed;
                }
            }

            targetSpeed = Math.Min(splineLookahead.MaxSpeed, targetSpeed);
            
            if (CurrentSpeed == 0 && !_stoppedForObstacle)
            {
                _stoppedForObstacle = true;
                _stoppedForObstacleSince = _sessionManager.ServerTimeMilliseconds;
                _obstacleHonkStart = _stoppedForObstacleSince + Random.Shared.Next(3000, 7000);
                _obstacleHonkEnd = _obstacleHonkStart + Random.Shared.Next(500, 1500);
                Log.Verbose("AI {SessionId} stopped for obstacle", EntryCar.SessionId);
            }
            else if (CurrentSpeed > 0 && _stoppedForObstacle)
            {
                _stoppedForObstacle = false;
                Log.Verbose("AI {SessionId} no longer stopped for obstacle", EntryCar.SessionId);
            }
            else if (_stoppedForObstacle && _sessionManager.ServerTimeMilliseconds - _stoppedForObstacleSince > _configuration.Extra.AiParams.IgnoreObstaclesAfterMilliseconds)
            {
                _ignoreObstaclesUntil = _sessionManager.ServerTimeMilliseconds + 10_000;
                Log.Verbose("AI {SessionId} ignoring obstacles until {IgnoreObstaclesUntil}", EntryCar.SessionId, _ignoreObstaclesUntil);
            }

            float deceleration = EntryCar.AiDeceleration;
            if (!hasObstacle)
            {
                deceleration *= EntryCar.AiCorneringBrakeForceFactor;
            }
            
            SetTargetSpeed(targetSpeed, deceleration, EntryCar.AiAcceleration);
        }

        public void StopForCollision()
        {
            _stoppedForCollisionUntil = _sessionManager.ServerTimeMilliseconds + Random.Shared.Next(_configuration.Extra.AiParams.MinCollisionStopTimeMilliseconds, _configuration.Extra.AiParams.MaxCollisionStopTimeMilliseconds);
        }

        public float GetAngleToCar(CarStatus car)
        {
            float challengedAngle = (float) (Math.Atan2(Status.Position.X - car.Position.X, Status.Position.Z - car.Position.Z) * 180 / Math.PI);
            if (challengedAngle < 0)
                challengedAngle += 360;
            float challengedRot = Status.GetRotationAngle();

            challengedAngle += challengedRot;
            challengedAngle %= 360;

            return challengedAngle;
        }

        private void SetTargetSpeed(float speed, float deceleration, float acceleration)
        {
            TargetSpeed = speed;
            if (speed < CurrentSpeed)
            {
                Acceleration = -deceleration;
            }
            else if (speed > CurrentSpeed)
            {
                Acceleration = acceleration;
            }
            else
            {
                Acceleration = 0;
            }
        }

        private void SetTargetSpeed(float speed)
        {
            SetTargetSpeed(speed, EntryCar.AiDeceleration, EntryCar.AiAcceleration);
        }

        public void Update()
        {
            if (!Initialized)
                return;

            long currentTime = _sessionManager.ServerTimeMilliseconds;
            long dt = currentTime - _lastTick;
            _lastTick = currentTime;

            if (Acceleration != 0)
            {
                CurrentSpeed += Acceleration * (dt / 1000.0f);
                
                if ((Acceleration < 0 && CurrentSpeed < TargetSpeed) || (Acceleration > 0 && CurrentSpeed > TargetSpeed))
                {
                    CurrentSpeed = TargetSpeed;
                    Acceleration = 0;
                }
            }

            float moveMeters = (dt / 1000.0f) * CurrentSpeed;
            if (!Move(_currentVecProgress + moveMeters) || !MapView.TryNext(CurrentSplinePoint, out var nextPoint))
            {
                Log.Debug("Car {SessionId} reached spline end, despawning", EntryCar.SessionId);
                Initialized = false;
                return;
            }

            CatmullRom.CatmullRomPoint smoothPos = CatmullRom.Evaluate(CurrentSplinePoint.Position, 
                nextPoint.Position, 
                _startTangent, 
                _endTangent, 
                _currentVecProgress / _currentVecLength);
            
            Vector3 rotation = new Vector3()
            {
                X = MathF.Atan2(smoothPos.Tangent.Z, smoothPos.Tangent.X) - MathF.PI / 2,
                Y = (MathF.Atan2(new Vector2(smoothPos.Tangent.Z, smoothPos.Tangent.X).Length(), smoothPos.Tangent.Y) - MathF.PI / 2) * -1f,
                Z = CurrentSplinePoint.GetCamber(_currentVecProgress / _currentVecLength)
            };
            
            float tyreAngularSpeed = GetTyreAngularSpeed(CurrentSpeed, 0.65f);
            byte encodedTyreAngularSpeed =  (byte) (Math.Clamp(MathF.Round(MathF.Log10(tyreAngularSpeed + 1.0f) * 20.0f) * Math.Sign(tyreAngularSpeed), -100.0f, 154.0f) + 100.0f);

            Status.Timestamp = _sessionManager.ServerTimeMilliseconds;
            Status.Position = smoothPos.Position with { Y = smoothPos.Position.Y + EntryCar.AiSplineHeightOffsetMeters };
            Status.Rotation = rotation;
            Status.Velocity = smoothPos.Tangent * CurrentSpeed;
            Status.SteerAngle = 127;
            Status.WheelAngle = 127;
            Status.TyreAngularSpeed[0] = encodedTyreAngularSpeed;
            Status.TyreAngularSpeed[1] = encodedTyreAngularSpeed;
            Status.TyreAngularSpeed[2] = encodedTyreAngularSpeed;
            Status.TyreAngularSpeed[3] = encodedTyreAngularSpeed;
            Status.EngineRpm = (ushort)MathUtils.Lerp(EntryCar.AiIdleEngineRpm, EntryCar.AiMaxEngineRpm, CurrentSpeed / _configuration.Extra.AiParams.MaxSpeedMs);
            Status.StatusFlag = CarStatusFlags.LightsOn
                                | CarStatusFlags.HighBeamsOff
                                | (_sessionManager.ServerTimeMilliseconds < _stoppedForCollisionUntil || CurrentSpeed < 20 / 3.6f ? CarStatusFlags.HazardsOn : 0)
                                | (CurrentSpeed == 0 || Acceleration < 0 ? CarStatusFlags.BrakeLightsOn : 0)
                                | (_stoppedForObstacle && _sessionManager.ServerTimeMilliseconds > _obstacleHonkStart && _sessionManager.ServerTimeMilliseconds < _obstacleHonkEnd ? CarStatusFlags.Horn : 0)
                                | GetWiperSpeed(_weatherManager.CurrentWeather.RainIntensity)
                                | _indicator;
            Status.Gear = 2;
        }
        
        private static float GetTyreAngularSpeed(float speed, float wheelDiameter)
        {
            return speed / (MathF.PI * wheelDiameter) * 6;
        }

        private static CarStatusFlags GetWiperSpeed(float rainIntensity)
        {
            return rainIntensity switch
            {
                < 0.05f => 0,
                < 0.25f => CarStatusFlags.WiperLevel1,
                < 0.5f => CarStatusFlags.WiperLevel2,
                _ => CarStatusFlags.WiperLevel3
            };
        }
    }
}