// Mental note:
// It is not entirely clear which .NET version should KSP plugins be compiled for,
// but the consensus is that 3.5 is the most appropriate because types introduced
// in 4.0 can be verified not to work. It is a fact that you can use C#4 itself
// with it with no ill effects, though -- at least all the features which rely
// on the compiler, rather than on the libraries.
// SCANsat is compiled for .NET 4.0 for some reason, which means that
// this assembly also needs to be compiled for 4.0 to link to it.
// The immediate drawback so far discovered is that I can't use LINQ,
// while when I compile for 3.5 I can.
// I wish there were some clarity on the subject.
using SCANsat;
using UnityEngine;
using System;
using System.Collections.Generic;

namespace SCANsatRPM
{
	public class JSISCANsatRPM: InternalModule
	{
		[KSPField]
		public int buttonUp;
		[KSPField]
		public int buttonDown = 1;
		[KSPField]
		public int buttonEnter = 2;
		[KSPField]
		public int buttonEsc = 3;
		[KSPField]
		public int maxZoom = 20;
		[KSPField]
		public float iconPixelSize = 8f;
		[KSPField]
		public Vector2 iconShadowShift = new Vector2(1, 1);
		[KSPField]
		public float redrawEdge = 0.8f;
		[KSPField]
		public Color iconColorSelf = Color.white;
		[KSPField]
		public Color iconColorTarget = Color.yellow;
		[KSPField]
		public Color iconColorUnvisitedAnomaly = Color.red;
		[KSPField]
		public Color iconColorVisitedAnomaly = Color.green;
		[KSPField]
		public Color iconColorShadow = Color.black;
		[KSPField]
		public float zoomModifier = 1.5f;
		[KSPField]
		public string scaleBar;
		[KSPField]
		public string scaleLabels;
		[KSPField]
		public string scaleLevels = "500000,200000,100000,50000,20000,10000,5000,1000";
		[KSPField]
		public Vector2 scaleBarPosition = new Vector2(16, 16);
		[KSPField]
		public float scaleBarSizeLimit = 512 / 2 - 16;
		[KSPField]
		public int trailLimit = 100;
		[KSPField]
		public Color trailColor = Color.blue;
		[KSPField]
		public double trailPointEvery = 10;
		// That ends our glut of configurable values.
		private int mapMode;
		private int zoomLevel;
		private int screenWidth;
		private int screenHeight;
		private double mapCenterLong, mapCenterLat;
		private SCANmap map;
		private CelestialBody orbitingBody;
		private Vessel targetVessel;
		private double redrawDeviation;
		private SCANdata.SCANanomaly[] localAnomalies;
		private Material iconMaterial;
		private JSI.PersistenceAccessor persistence;
		private string persistentVarName;
		private double pixelsPerKm;
		private Texture2D scaleBarTexture, scaleLabelTexture;
		private float[] scaleLevelValues;
		private float scaleLabelSpan;
		private readonly List<Vector2> trail = new List<Vector2>();
		private static readonly Material trailMaterial = new Material(Shader.Find("Particles/Additive"));
		private double trailCounter;

		public bool MapRenderer(RenderTexture screen)
		{
			// Just in case.
			if (!HighLogic.LoadedSceneIsFlight)
				return false;

			if (screenWidth == 0 || screenHeight == 0) {
				int? loadedMode = persistence.GetVar(persistentVarName + "mode");
				mapMode = loadedMode ?? 0;
				int? loadedZoom = persistence.GetVar(persistentVarName + "zoom");
				zoomLevel = loadedZoom ?? 1;
				int? loadedColors = persistence.GetVar(persistentVarName + "color");
				SCANcontroller.controller.colours = loadedColors ?? 0;
				screenWidth = screen.width;
				screenHeight = screen.height;
				iconMaterial = new Material(Shader.Find("KSP/Alpha/Unlit Transparent"));
				map = new SCANmap();
				map.setProjection(SCANmap.MapProjection.Rectangular);
				RedrawMap();
				return false;
			}

			Graphics.Blit(map.map, screen);
			GL.PushMatrix();
			GL.LoadPixelMatrix(0, screenWidth, screenHeight, 0);

			if (trailLimit > 0 && trail.Count > 0) {
				GL.wireframe = true;
				GL.Begin(GL.LINES);
				trailMaterial.SetPass(0);
				GL.Color(trailColor);
				double xStart = 0, yStart = 0, xEnd = 0, yEnd = 0;

				bool endsInVessel = false;
				for (int i = 0; i < trail.Count; i++) {
					xStart = longitudeToPixels(trail[i].x, trail[i].y);
					yStart = latitudeToPixels(trail[i].x, trail[i].y);
					if (i + 1 < trail.Count) {
						xEnd = longitudeToPixels(trail[i + 1].x, trail[i + 1].y);
						yEnd = latitudeToPixels(trail[i + 1].x, trail[i + 1].y);
					} else {
						xEnd = longitudeToPixels(vessel.longitude, vessel.latitude);
						yEnd = latitudeToPixels(vessel.longitude, vessel.latitude);
						endsInVessel = true;
					}
					DrawLine(xStart, yStart, xEnd, yEnd, screenWidth, screenHeight);
				}
				if (!endsInVessel)
					DrawLine(xEnd, yEnd, longitudeToPixels(vessel.longitude, vessel.latitude), latitudeToPixels(vessel.longitude, vessel.latitude), screenWidth, screenHeight);
				GL.End();
				GL.wireframe = false;
			}

			foreach (SCANdata.SCANanomaly anomaly in localAnomalies) {
				if (anomaly.known)
					DrawIcon(anomaly.longitude, anomaly.latitude,
						anomaly.detail ? (VesselType)int.MaxValue : VesselType.Unknown,
						anomaly.detail ? iconColorVisitedAnomaly : iconColorUnvisitedAnomaly);
			}
			if (targetVessel != null && targetVessel.mainBody == orbitingBody)
				DrawIcon(targetVessel.longitude, targetVessel.latitude, targetVessel.vesselType, iconColorTarget);
			DrawIcon(vessel.longitude, vessel.latitude, vessel.vesselType, iconColorSelf);
			DrawScale();
			GL.PopMatrix();

			return true;
		}

		private static void DrawLine(double xStart, double yStart, double xEnd, double yEnd, int screenWidth, int screenHeight)
		{
			// Normally I wouldn't have to, but in some cases the lines get drawn across the screen,
			// and I don't feel like digging around to figure out why right now.
			// So the easiest way to get rid of it is to just not to draw the lines that
			// start outside the screen..
			if (xStart < screenWidth && yStart < screenHeight) {
				GL.Vertex3((float)xStart, (float)yStart, 0);
				GL.Vertex3((float)xEnd, (float)yEnd, 0);
			}
		}

		private void DrawScale()
		{
			if (scaleBarTexture == null || scaleLabelTexture == null)
				return;

			var scaleBarRect = new Rect();
			scaleBarRect.x = scaleBarPosition.x;
			scaleBarRect.height = scaleLabelTexture.height / scaleLevelValues.Length;
			scaleBarRect.y = screenHeight - scaleBarPosition.y - scaleBarRect.height;

			int scaleID = 0;
			for (int i = scaleLevelValues.Length; i-- > 0;) {
				if (scaleLevelValues[i] * pixelsPerKm < scaleBarSizeLimit) {
					scaleBarRect.width = (float)(scaleLevelValues[i] * pixelsPerKm);
					scaleID = i;
					break;
				}
			}
			Graphics.DrawTexture(scaleBarRect, scaleBarTexture, new Rect(0, 0, 1f, 1f), 4, 4, 4, 4);

			scaleBarRect.x += scaleBarRect.width;
			scaleBarRect.width = scaleLabelTexture.width;
			Graphics.DrawTexture(scaleBarRect, scaleLabelTexture, new Rect(0f, scaleID * scaleLabelSpan, 1f, scaleLabelSpan), 0, 0, 0, 0);
		}

		private void DrawIcon(double longitude, double latitude, VesselType vt, Color iconColor)
		{
			var position = new Rect((float)(longitudeToPixels(longitude, latitude) - iconPixelSize / 2),
				               (float)(latitudeToPixels(longitude, latitude) - iconPixelSize / 2),
				               iconPixelSize, iconPixelSize);

			Rect shadow = position;
			shadow.x += iconShadowShift.x;
			shadow.y += iconShadowShift.y;

			iconMaterial.color = iconColorShadow;
			Graphics.DrawTexture(shadow, MapView.OrbitIconsMap, VesselTypeIcon(vt), 0, 0, 0, 0, iconMaterial);

			iconMaterial.color = iconColor;
			Graphics.DrawTexture(position, MapView.OrbitIconsMap, VesselTypeIcon(vt), 0, 0, 0, 0, iconMaterial);
		}

		private double longitudeToPixels(double longitude, double latitude)
		{
			return rescaleLongitude((map.projectLongitude(longitude, latitude) + 180) % 360) * screenWidth / 360;
		}

		private double latitudeToPixels(double longitude, double latitude)
		{
			return screenHeight - (rescaleLatitude((map.projectLatitude(longitude, latitude) + 90) % 180) * screenHeight / 180);
		}

		private double rescaleLatitude(double lat)
		{
			lat = Clamp(lat - map.lat_offset, 180);
			lat *= 180f / (map.mapheight / map.mapscale);
			return lat;
		}

		private double rescaleLongitude(double lon)
		{
			lon = Clamp(lon - map.lon_offset, 360);
			lon *= 360f / (map.mapwidth / map.mapscale);
			return lon;
		}

		private static double Clamp(double value, double clamp)
		{
			value = value % clamp;
			if (value < 0)
				return value + clamp;
			return value;
		}

		private static Rect VesselTypeIcon(VesselType type)
		{
			int x, y;
			const float symbolSpan = 0.2f;
			switch (type) {
				case VesselType.Base:
					x = 2;
					y = 0;
					break;
				case VesselType.Debris:
					x = 1;
					y = 3;
					break;
				case VesselType.EVA:
					x = 2;
					y = 2;
					break;
				case VesselType.Flag:
					x = 4;
					y = 0;
					break;
				case VesselType.Lander:
					x = 3;
					y = 0;
					break;
				case VesselType.Probe:
					x = 1;
					y = 0;
					break;
				case VesselType.Rover:
					x = 0;
					y = 0;
					break;
				case VesselType.Ship:
					x = 0;
					y = 3;
					break;
				case VesselType.Station:
					x = 3;
					y = 1;
					break;
				case VesselType.Unknown:
					x = 3;
					y = 3;
					break;
				default:
					x = 3;
					y = 2;
					break;
			}
			var result = new Rect();
			result.x = symbolSpan * x;
			result.y = symbolSpan * y;
			result.height = result.width = symbolSpan;
			return result;
		}

		public void ButtonProcessor(int buttonID)
		{
			if (screenWidth == 0 || screenHeight == 0)
				return;
			if (buttonID == buttonUp) {
				ChangeZoom(false);
			}
			if (buttonID == buttonDown) {
				ChangeZoom(true);
			}
			if (buttonID == buttonEnter) {
				ChangeMapMode(true);
			}
			if (buttonID == buttonEsc) {
				// Whatever possessed him to do THAT?
				SCANcontroller.controller.colours = SCANcontroller.controller.colours == 0 ? 1 : 0;
				persistence.SetVar(persistentVarName + "color", SCANcontroller.controller.colours);
				RedrawMap();
			}
		}

		private void ChangeMapMode(bool up)
		{
			mapMode += up ? 1 : -1;

			if (mapMode > 2)
				mapMode = 0;
			if (mapMode < 0)
				mapMode = 2;
			persistence.SetVar(persistentVarName + "mode", mapMode);
			RedrawMap();
		}

		private void ChangeZoom(bool up)
		{
			int oldZoom = zoomLevel;
			zoomLevel += up ? 1 : -1;
			if (zoomLevel < 0)
				zoomLevel = 0;
			if (zoomLevel > maxZoom)
				zoomLevel = maxZoom;
			if (zoomLevel != oldZoom) {
				persistence.SetVar(persistentVarName + "zoom", zoomLevel);
				RedrawMap();
			}
		}

		public override void OnUpdate()
		{
			if (!HighLogic.LoadedSceneIsFlight || vessel != FlightGlobals.ActiveVessel)
				return;

			if ((vessel.missionTime - trailPointEvery) > trailCounter) {
				trailCounter = vessel.missionTime;
				LeaveTrail();
			}

			if (!(CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.IVA ||
			    CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.Internal))
				return;

			if (map != null && !map.isMapComplete()) {
				map.getPartialMap();
			}

			targetVessel = FlightGlobals.fetch.VesselTarget as Vessel;

			if (UpdateCheck() || orbitingBody != vessel.mainBody) {
				if (orbitingBody != vessel.mainBody)
					trail.Clear();
				RedrawMap();
			}
		}

		private void RedrawMap()
		{
			orbitingBody = vessel.mainBody;
			map.setBody(vessel.mainBody);
			map.setSize(screenWidth, screenHeight);
			map.mapscale *= (Math.Pow(zoomLevel, 2) + zoomModifier);
			mapCenterLong = vessel.longitude;
			mapCenterLat = vessel.latitude;
			map.centerAround(mapCenterLong, mapCenterLat);
			map.resetMap(mapMode);
			redrawDeviation = redrawEdge * 180 / (Math.Pow(zoomLevel, 2) + zoomModifier);
			localAnomalies = SCANcontroller.controller.getData(vessel.mainBody).getAnomalies();
			// MATH!
			double kmPerDegreeLon = (2 * Math.PI * (orbitingBody.Radius / 1000d)) / 360d;
			double pixelsPerDegree = Math.Abs(longitudeToPixels(mapCenterLong + (((mapCenterLong + 1) > 360) ? -1 : 1), mapCenterLat) - longitudeToPixels(mapCenterLong, mapCenterLat));
			pixelsPerKm = pixelsPerDegree / kmPerDegreeLon;

		}

		private bool UpdateCheck()
		{
			if (map == null)
				return false;
			if ((Math.Abs(vessel.latitude - mapCenterLat) > redrawDeviation) ||
			    (Math.Abs(vessel.longitude - mapCenterLong) > redrawDeviation))
				return true;

			return false;
		}

		private void LeaveTrail()
		{
			if (trailLimit > 0) {
				trail.Add(new Vector2((float)vessel.longitude, (float)vessel.latitude));
				if (trail.Count > trailLimit)
					trail.RemoveRange(0, trail.Count - trailLimit);
			}
		}

		private void Start()
		{
			// Referencing the parent project should work, shouldn't it.
			persistentVarName = "scansat" + internalProp.propID;
			persistence = new JSI.PersistenceAccessor(part);

			LeaveTrail();

			if (!string.IsNullOrEmpty(scaleBar) && !string.IsNullOrEmpty(scaleLabels) && !string.IsNullOrEmpty(scaleLevels)) {
				scaleBarTexture = GameDatabase.Instance.GetTexture(scaleBar, false);
				scaleLabelTexture = GameDatabase.Instance.GetTexture(scaleLabels, false);
				var scales = new List<float>();
				foreach (string scl in scaleLevels.Split(',')) {
					float scale;
					if (float.TryParse(scl.Trim(), out scale))
						scales.Add(scale / 1000);

				}
				scaleLevelValues = scales.ToArray();
				Array.Sort(scaleLevelValues);
				scaleLabelSpan = 1f / scaleLevelValues.Length;
			}
		}
	}
}


