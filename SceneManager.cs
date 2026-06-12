using MegaSwarm.Core;
using MegaSwarm.Core.Graphics;
using System.Collections.Generic;
using MegaSwarm.Simulations.PAM.Config;
using System.Linq;
using MegaSwarm.Simulations.PAM.Data;
using System;
using MegaSwarm.Simulations.PAM.Subswarms;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;

namespace MegaSwarm.Simulations.PAM
{
    internal class SceneManager : CoreComponent
    {
        //Heatmap 
        internal int[,] Heatmap;
        private int mapWidth = 64;
        private int mapHeight = 64;
        public int MapWidth => mapWidth;
        public int MapHeight => mapHeight;

        // Scene state 
        protected SimConfig _Config;
        protected Pambot[] _Robots;
        protected List<Pambot> _ArrivedRobots;
        private Site[] _Sites;
        private Map _Map;

        internal protected List<Sample> Samples { get; private set; } = new List<Sample>();
        internal protected Sample FinalSample => Samples[Samples.Count - 1];

        // subswarm data
        protected SubswarmAllocation _IdealSubswarms;
        protected int _MaxSubswarmsAllocated = 0;
        protected float _SubswarmAllocationScore = 0;

        public SceneManager(SimConfig config) { _Config = config; }

        public SceneManager(string name, SimConfig config)
        {
            _Config = config;
            Setup(name);
        }

        // Setup
        protected void Setup(string name)
        {
            CoreObject obj = new CoreObject(name);
            obj.AddComponent(this);

            CoreScene scene = new CoreScene(_Config, name, _Config.RandomSeed)
            {
                MaxTicks = _Config.MaxTicks
            };
            scene.AddObject(this);
            scene.Logger.Verbosity = _Config.LogVerbosity;

            scene.Logger.Log($"Control Mode: {_Config.ControlMode}");

            // Camera
            Camera camera = new Camera(8);
            Scene.AddObject(camera);
            Scene.Camera = camera;

            // Map
            _Map = new Map(_Config.Map);
            Scene.AddObject(_Map);
            camera.Map = _Map;


            // Heatmap grid
            Heatmap = new int[mapWidth, mapHeight];

            // World
            CreateSites();
            CreateRobots();
            CreateIdealSubswarms();
        }

        // Tick
        protected override void FinalUpdate()
        {
            int ticks = Scene.Ticks + 1;

            if (_Config.SampleRate > 0 && (ticks % _Config.SampleRate == 0))
                Sample(ticks);

            if (_Config.RobotKills != null)
            {
                foreach (RobotKillConfig kill in _Config.RobotKills)
                    if (kill.Time == ticks) KillRobots(kill);
            }

            Scene.Progress = (ticks * 100) / Scene.MaxTicks;

            bool endEarly = true;
            foreach (Site site in _Sites)
            {
                if (site.Score < 1) { endEarly = false; break; }
            }
            endEarly = endEarly || (Scene.SwarmSize == 0);

            if (endEarly) Scene.End();
        }

        // End of scene 
        protected override void SceneFinish()
        {
            // Final sample
            if (_Config.SampleRate == 0 || Scene.Ticks % _Config.SampleRate > 0)
                Sample(Scene.Ticks);

            FinalSample.Log(Scene.Logger);

            // Export CSV
            string csvPath = Path.Combine(Environment.CurrentDirectory, "heatmap.csv");
            using (var writer = new StreamWriter(csvPath))
            {
                for (int y = 0; y < mapHeight; y++)
                {
                    string[] row = new string[mapWidth];
                    for (int x = 0; x < mapWidth; x++)
                        row[x] = Heatmap[x, y].ToString();
                    writer.WriteLine(string.Join(",", row));
                }
            }
            Scene.Logger.Log($"Heatmap exported to: {csvPath}");

            // Export image
            ExportHeatmapAsImage();
        }

        // Heatmap visualisation 
        private void ExportHeatmapAsImage(int cellSize = 5)
        {
            string path = Path.Combine(Environment.CurrentDirectory, "heatmap.png");
            int width = mapWidth * cellSize;
            int height = mapHeight * cellSize;

            using (var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.Black);

                // find max
                int max = 0;
                for (int y = 0; y < mapHeight; y++)
                    for (int x = 0; x < mapWidth; x++)
                        if (Heatmap[x, y] > max) max = Heatmap[x, y];

                if (max == 0)
                {
                    bmp.Save(path, ImageFormat.Png);
                    Scene.Logger.Log($"Heatmap image exported to: {path}");
                    return;
                }

                // draw each cell
                for (int y = 0; y < mapHeight; y++)
                {
                    for (int x = 0; x < mapWidth; x++)
                    {
                        float t = Heatmap[x, y] / (float)max; // 0..1
                        var c = HeatColor(t);
                        using (var br = new SolidBrush(c))
                            g.FillRectangle(br, x * cellSize, y * cellSize, cellSize, cellSize);
                    }
                }

                bmp.Save(path, ImageFormat.Png);
            }

            Scene.Logger.Log($"Heatmap image exported to: {path}");
        }

        private System.Drawing.Color HeatColor(float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            // black -> blue -> cyan -> green -> yellow -> red
            if (t < 0.25f)
            {
                float k = t / 0.25f;
                return System.Drawing.Color.FromArgb(0, 0, (int)(64 + 191 * k));           // black->blue
            }
            if (t < 0.5f)
            {
                float k = (t - 0.25f) / 0.25f;
                return System.Drawing.Color.FromArgb(0, (int)(255 * k), 255);              // blue->cyan
            }
            if (t < 0.75f)
            {
                float k = (t - 0.5f) / 0.25f;
                return System.Drawing.Color.FromArgb(0, 255, (int)(255 * (1 - k)));        // cyan->green
            }
            else
            {
                float k = (t - 0.75f) / 0.25f;
                return System.Drawing.Color.FromArgb((int)(255 * k), 255, 0);              // green->yellow->red start
            }
        }

        //Sites
        private void CreateSites()
        {
            List<Vector2> sitePositions;
            if (_Config.Sites.Positions != null && _Config.Sites.Positions.Count > 0)
            {
                sitePositions = new List<Vector2>(_Config.Sites.Positions.Count);
                foreach (string pos in _Config.Sites.Positions)
                    sitePositions.Add(new Vector2(pos));
            }
            else if (_Config.Sites.Circles != null && _Config.Sites.Circles.Count > 0)
            {
                sitePositions = new List<Vector2>();
                foreach (Config.SiteCircleConfig circle in _Config.Sites.Circles)
                    sitePositions.AddRange(circle.CreateSitePositions());
            }
            else
            {
                sitePositions = new List<Vector2>();
                if (_Map.Contains(new Vector2(_Config.Sites.MinSpawnRange, 0)))
                {
                    while (sitePositions.Count < _Config.Sites.Count)
                    {
                        Vector2 pos = _Map.GetRandomPosition(true);
                        if (pos.Length() >= _Config.Sites.MinSpawnRange)
                            sitePositions.Add(pos);
                    }
                }
            }

            if (_Config.Sites.ExportFile != null)
            {
                List<string> stringPos = new List<string>(sitePositions.Count);
                foreach (Vector2 pos in sitePositions)
                    stringPos.Add(pos.ToShortString(false));
                JsonExt.Serialize(stringPos, _Config.Sites.ExportFile);
            }

            _Sites = new Site[sitePositions.Count];
            for (int i = 0; i < sitePositions.Count; i++)
            {
                Site site = new Site(i, _Config.Sites.Radius, _Config.Tasks) { Position = sitePositions[i] };
                Scene.AddObject(site);
                _Sites[i] = site;

                if (_Config.Sites.SpecialChance.HasValue && _Config.Sites.SpecialChance.Value > 0)
                    if (Scene.Random.Chance(_Config.Sites.SpecialChance.Value))
                        site.SetSpecial();
            }

            if (_Config.Sites.SpecialChance == null && _Config.Sites.SpecialCount.HasValue && _Config.Sites.SpecialCount.Value > 0)
            {
                Site[] specialSites = new List<Site>(_Sites).GetRandom(Scene.Random, _Config.Sites.SpecialCount.Value);
                foreach (Site site in specialSites) site.SetSpecial();
            }
        }

        internal List<SiteInfo> FindSites(Vector2 position, float range)
        {
            List<SiteInfo> sites = new List<SiteInfo>();
            foreach (Site site in _Sites)
            {
                Vector2 toSite = site.Position - position;
                if (toSite.Length() <= range)
                {
                    sites.Add(new SiteInfo(site.Id, site.Position, site.Radius));
                    site.Discovered = true;
                }
            }
            return sites;
        }

        internal Site GetSite(int id)
        {
            if (id < _Sites.Length) return _Sites[id];
            return null;
        }

        //Robots 
        private void CreateRobots()
        {
            int totalRobots = _Config.Robots.Counts.Sum();
            _Robots = new Pambot[totalRobots];

            int index = 0;
            for (int i = 0; i < _Config.Robots.Counts.Length; i++)
            {
                PambotType type = (PambotType)i;
                for (int j = 0; j < _Config.Robots.Counts[i]; j++)
                {
                    Pambot robot = new Pambot(_Config, index, type)
                    {
                        Position = Vector2.RandomWithinCircle(Scene.Random, Vector2.Zero, _Config.RobotSpawnRadius)
                    };

                    _Robots[index] = robot;
                    index++;

                    SetupRobot(robot);

                    if (_Config.TimeBombs != null)
                    {
                        foreach (TimeBombConfig bomb in _Config.TimeBombs)
                            if (bomb.CheckRobot(robot))
                                robot.Object.AddComponent(new TimeBomb(bomb, _Config.ControlMode));
                    }
                }
            }

            // Add robots randomly
            List<Pambot> shuffled = new List<Pambot>(_Robots);
            shuffled.Shuffle(Scene.Random);
            foreach (Pambot bot in shuffled) Scene.AddObject(bot);

            // Initial kills
            if (_Config.RobotKills != null)
            {
                foreach (RobotKillConfig kill in _Config.RobotKills)
                    if (kill.Time == 0) KillRobots(kill);
            }

            // Arrivals
            _ArrivedRobots = new List<Pambot>();
            foreach (Pambot bot in _Robots)
            {
                if (bot.Scene == null) bot.Arrived = false;
                else _ArrivedRobots.Add(bot);
            }
        }

        private void KillRobots(RobotKillConfig kill)
        {
            List<Pambot> bots = _Robots.Where(b => (kill.Type == null || b.Type == kill.Type.Value) && b.Scene != null).ToList();
            Pambot[] toKill = null;

            if (kill.Count > 0)
            {
                toKill = bots.GetRandom(Scene.Random, kill.Count);
            }
            else if (kill.Ids != null)
            {
                toKill = new Pambot[kill.Ids.Length];
                for (int i = 0; i < kill.Ids.Length; i++)
                    toKill[i] = bots.Find(b => b.Id == kill.Ids[i]);
            }

            if (toKill != null)
            {
                int killed = 0;
                foreach (Pambot bot in toKill)
                {
                    if (bot != null) { bot.Kill(); killed++; }
                }

                if (kill.Type.HasValue) Scene.Logger.Log($"{killed} robots of Type {kill.Type} killed");
                else Scene.Logger.Log($"{killed} robots of any type killed");
            }
        }

        protected virtual void SetupRobot(Pambot robot) { }

        internal Pambot GetRobot(int id)
        {
            if (id >= 0 && id < _Robots.Length) return _Robots[id];
            return null;
        }

        private void CreateIdealSubswarms()
        {
            IEnumerable<Pambot> pambots = _Config.PreAssignSubswarms ? _Robots : _ArrivedRobots;
            List<RobotInfo> robots = new List<RobotInfo>(pambots.Count());
            foreach (Pambot bot in pambots) robots.Add(bot.Info);

            _IdealSubswarms = new SubswarmAllocation(_Config.Subswarms, robots, Vector2.Zero, 0, Scene);
            Scene.Logger.Log($"Ideal subswarm count: {_IdealSubswarms.Count}");
        }

        //Samples 
        private void Sample(int ticks)
        {
            Sample sample = new Sample(ticks);
            UpdateSample(sample);
            Samples.Add(sample);
        }

        protected virtual void UpdateSample(Sample sample)
        {
            int sitesDiscovered = 0, sitesVisited = 0, specialSitesDiscovered = 0, specialSites = 0;
            float totalSiteScore = 0, totalBasicScore = 0, totalSpecialScore = 0, totalWastedEffort = 0;
            int claims = 0, claimSuccesses = 0;
            int[] totalSpecialContributions = new int[Enum.GetValues(typeof(PambotType)).Length];

            foreach (Site site in _Sites)
            {
                if (site.Discovered) sitesDiscovered++;
                if (site.Visited) sitesVisited++;
                if (site.DiscoveredSpecial) specialSitesDiscovered++;
                if (site.IsSpecial)
                {
                    specialSites++;
                    for (int i = 0; i < site.SpecialContributions.Length; i++)
                        totalSpecialContributions[i] += site.SpecialContributions[i];
                }

                totalSiteScore += site.Score;
                totalBasicScore += site.BasicScore;
                totalSpecialScore += site.SpecialScore;
                totalWastedEffort += site.WastedEffort;
                claims += site.Claims;
                claimSuccesses += site.SuccessfulClaims;
            }

            int totalActiveRobots = 0;
            int[] aliveRobots = new int[Enum.GetValues(typeof(PambotType)).Length];

            foreach (Pambot bot in _ArrivedRobots)
            {
                if (bot.IsAlive)
                {
                    totalActiveRobots++;
                    aliveRobots[(int)bot.Type]++;
                }
            }

            sample.SitesDiscovered = sitesDiscovered;
            sample.SitesVisited = sitesVisited;
            sample.SpecialSites = specialSites;
            sample.SpecialSitesDiscovered = specialSitesDiscovered;
            sample.MeanSiteScore = totalSiteScore / _Sites.Length;
            sample.MeanVisitedSiteScore = (sitesVisited > 0) ? totalSiteScore / sitesVisited : 0;
            sample.MeanBasicScore = totalBasicScore / _Sites.Length;
            sample.MeanSpecialScore = (specialSites > 0) ? totalSpecialScore / specialSites : 0;
            sample.WastedEffortScore = totalWastedEffort / _Sites.Length;
            sample.SiteClaims = claims;
            sample.SiteSuccessfulClaims = claimSuccesses;

            sample.SpecialContributions = totalSpecialContributions;
            sample.SubswarmAllocationScore = _SubswarmAllocationScore;

            CalculateAssignedRobotsScore(out float assignedRobotsScore, out int unassignedRobots);
            sample.AssignedRobotScore = assignedRobotsScore;
            sample.UnassignedRobots = unassignedRobots;

            sample.AllocatedSubswarms = _MaxSubswarmsAllocated;

            AuditSubswarms(out int activeSubswarms, out int fullyCapableSubswarms, out int subswarmsWithLeader);
            sample.ActiveSubswarms = activeSubswarms;
            sample.FullyCapableSubswarms = fullyCapableSubswarms;
            sample.SubswarmsWithLeader = subswarmsWithLeader;

            sample.ActiveRobotsTotal = totalActiveRobots;
            sample.AliveRobots = aliveRobots;

            RobotSample robotSample = new RobotSample();
            foreach (Pambot bot in _ArrivedRobots)
                robotSample.Add(bot.Sample(), bot.Type == PambotType.Messenger, bot.IsAlive);
            sample.RobotSample = robotSample;
        }

        protected virtual void CalculateAssignedRobotsScore(out float assignedRobotsScore, out int unassignedRobots)
        {
            assignedRobotsScore = 0;
            unassignedRobots = 0;
        }

        protected virtual void AuditSubswarms(out int active, out int fullyCapable, out int withLeader)
        {
            active = 0;
            fullyCapable = 0;
            withLeader = 0;
        }
    } 
} 
