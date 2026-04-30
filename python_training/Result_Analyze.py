import os
import glob
import matplotlib.pyplot as plt
from tensorboard.backend.event_processing import event_accumulator

def analyze_and_export_tensorboard(log_dir, export_dir="graphs"):
    os.makedirs(export_dir, exist_ok=True)
    
    # Find all event files
    event_files = glob.glob(os.path.join(log_dir, '**', '*tfevents*'), recursive=True)
    if not event_files:
        print("No event files found in results.")
        return
        
    print(f"Found {len(event_files)} event files. Processing all runs...")
    
    # Dictionary to hold data: all_data[tag][run_name] = (steps, values)
    all_data = {}
    
    for event_file in event_files:
        # Extract run name from directory structure
        # E.g. results\CarRacing_32x32\logs\... -> "CarRacing_32x32"
        rel_path = os.path.relpath(event_file, log_dir)
        run_name = rel_path.split(os.sep)[0]
        
        # Skip the export directory itself if somehow tfevents ended up there
        if run_name == os.path.basename(export_dir):
            continue

        try:
            ea = event_accumulator.EventAccumulator(event_file)
            ea.Reload()
            
            tags = ea.Tags().get('scalars', [])
            for tag in tags:
                events = ea.Scalars(tag)
                if not events: continue
                
                steps = [e.step for e in events]
                values = [e.value for e in events]
                
                if tag not in all_data:
                    all_data[tag] = {}
                
                # If a run has multiple event files, we'll keep the longest one to avoid stitching logic complexities
                if run_name not in all_data[tag] or len(steps) > len(all_data[tag][run_name][0]):
                    all_data[tag][run_name] = (steps, values)
        except Exception as e:
            print(f"Error processing {event_file}: {e}")

    print(f"\nExporting combined graphs...")
    
    for tag, runs in all_data.items():
        plt.figure(figsize=(12, 7))
        
        for run_name, (steps, values) in runs.items():
            # Smoothing the lines slightly might be nice for docs, but we'll stick to raw for accuracy
            plt.plot(steps, values, label=run_name, linewidth=1.5, alpha=0.8)
            
        # Styling
        tag_display = tag.split('/')[-1].replace('_', ' ').title()
        plt.title(f"Comparison: {tag_display}", fontsize=14, pad=15)
        plt.xlabel("Training Steps", fontsize=12)
        plt.ylabel("Value", fontsize=12)
        plt.grid(True, linestyle='--', alpha=0.7)
        
        # Place legend outside if there are many runs
        plt.legend(bbox_to_anchor=(1.04, 1), loc="upper left", fontsize=10)
        
        # Format large numbers on x-axis with commas
        plt.gca().xaxis.set_major_formatter(plt.matplotlib.ticker.StrMethodFormatter('{x:,.0f}'))
        
        # Save Graph
        safe_tag_name = tag.replace('/', '_')
        export_path = os.path.join(export_dir, f"{safe_tag_name}_comparison.png")
        plt.savefig(export_path, dpi=300, bbox_inches='tight')
        plt.close() # Close to free up memory
        
        print(f"Exported: {export_path}")

if __name__ == '__main__':
    log_directory = r'd:\Projects\3D AI Racing\results'
    export_directory = r'd:\Projects\3D AI Racing\results\exported_graphs'
    analyze_and_export_tensorboard(log_directory, export_directory)

